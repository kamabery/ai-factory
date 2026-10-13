using System.Net;
using System.Net.Http.Json;
using shelf.api.tests.Infrastructure;
using shelf.data;

namespace shelf.api.tests.Harness;

/// <summary>
/// Drives the Todo endpoints through the real host on in-memory SQLite, as
/// <see cref="BooksControllerTests"/> does for Book.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class TodosControllerTests : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new ApiFactory();
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    // AC3

    [Fact]
    public async Task GetAll_ReturnsEmptyArray_WhenNoTodos()
    {
        var response = await _client.GetAsync("/Todos");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe("[]");
    }

    [Fact]
    public async Task GetAll_ReturnsCreatedTodos()
    {
        await CreateAsync("first");
        await CreateAsync("second");

        var todos = await _client.GetFromJsonAsync<List<Todo>>("/Todos");

        todos.ShouldNotBeNull();
        todos!.Select(t => t.Title).ShouldBe(["first", "second"], ignoreOrder: true);
    }

    // AC4

    [Fact]
    public async Task GetById_ReturnsTodo()
    {
        var created = await CreateAsync("read me");

        var response = await _client.GetAsync($"/Todos/{created.Id}");
        var todo = await response.Content.ReadFromJsonAsync<Todo>();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        todo.ShouldNotBeNull();
        todo!.Id.ShouldBe(created.Id);
        todo.Title.ShouldBe("read me");
        todo.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task GetById_Returns404_ForUnknownId()
    {
        (await _client.GetAsync("/Todos/9999")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // AC5

    [Fact]
    public async Task Create_Returns201WithLocation()
    {
        // A client-supplied id is ignored: the database assigns it.
        var response = await _client.PostAsJsonAsync(
            "/Todos", new { id = 4242, title = "new", isCompleted = true });
        var todo = await response.Content.ReadFromJsonAsync<Todo>();

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        todo.ShouldNotBeNull();
        todo!.Id.ShouldBeGreaterThan(0);
        todo.Id.ShouldNotBe(4242);
        todo.Title.ShouldBe("new");
        todo.IsCompleted.ShouldBeTrue();

        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location!.ToString().ShouldEndWith($"/Todos/{todo.Id}", Case.Insensitive);
        (await _client.GetAsync(response.Headers.Location)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_Returns400_ForBlankTitle(string title)
    {
        var response = await _client.PostAsJsonAsync("/Todos", new { title, isCompleted = false });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _client.GetFromJsonAsync<List<Todo>>("/Todos"))!.ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_Returns400_ForTooLongTitle()
    {
        var ok = await _client.PostAsJsonAsync(
            "/Todos", new { title = new string('a', 200), isCompleted = false });
        var tooLong = await _client.PostAsJsonAsync(
            "/Todos", new { title = new string('a', 201), isCompleted = false });

        ok.StatusCode.ShouldBe(HttpStatusCode.Created);
        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // AC6

    [Fact]
    public async Task Update_ChangesTitleAndCompleted()
    {
        var created = await CreateAsync("before");

        var response = await _client.PutAsJsonAsync(
            $"/Todos/{created.Id}", new { id = created.Id, title = "after", isCompleted = true });
        var todo = await response.Content.ReadFromJsonAsync<Todo>();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        todo!.Id.ShouldBe(created.Id);
        todo.Title.ShouldBe("after");
        todo.IsCompleted.ShouldBeTrue();

        var stored = await _client.GetFromJsonAsync<Todo>($"/Todos/{created.Id}");
        stored!.Title.ShouldBe("after");
        stored.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task Update_RouteIdWins_OverBodyId()
    {
        var target = await CreateAsync("target");
        var other = await CreateAsync("other");

        var response = await _client.PutAsJsonAsync(
            $"/Todos/{target.Id}", new { id = other.Id, title = "changed", isCompleted = false });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.GetFromJsonAsync<Todo>($"/Todos/{target.Id}"))!.Title.ShouldBe("changed");
        (await _client.GetFromJsonAsync<Todo>($"/Todos/{other.Id}"))!.Title.ShouldBe("other");
    }

    [Fact]
    public async Task Update_Returns404_ForUnknownId()
    {
        var response = await _client.PutAsJsonAsync(
            "/Todos/9999", new { title = "x", isCompleted = false });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Update_Returns400_ForBlankTitle(string title)
    {
        var created = await CreateAsync("keep");

        var response = await _client.PutAsJsonAsync(
            $"/Todos/{created.Id}", new { title, isCompleted = true });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _client.GetFromJsonAsync<Todo>($"/Todos/{created.Id}"))!.Title.ShouldBe("keep");
    }

    // AC7

    [Fact]
    public async Task SetCompleted_ChecksOffAndUnchecks()
    {
        var created = await CreateAsync("check me");

        var on = await SetCompletedAsync(created.Id, true);
        var onAgain = await SetCompletedAsync(created.Id, true);
        on.StatusCode.ShouldBe(HttpStatusCode.OK);
        onAgain.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await onAgain.Content.ReadFromJsonAsync<Todo>())!.IsCompleted.ShouldBeTrue();
        (await _client.GetFromJsonAsync<Todo>($"/Todos/{created.Id}"))!.IsCompleted.ShouldBeTrue();

        var off = await SetCompletedAsync(created.Id, false);
        off.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await off.Content.ReadFromJsonAsync<Todo>())!.IsCompleted.ShouldBeFalse();
        (await _client.GetFromJsonAsync<Todo>($"/Todos/{created.Id}"))!.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task SetCompleted_LeavesTitleUnchanged()
    {
        var created = await CreateAsync("same title");

        var response = await SetCompletedAsync(created.Id, true);

        (await response.Content.ReadFromJsonAsync<Todo>())!.Title.ShouldBe("same title");
        (await _client.GetFromJsonAsync<Todo>($"/Todos/{created.Id}"))!.Title.ShouldBe("same title");
    }

    [Fact]
    public async Task SetCompleted_Returns404_ForUnknownId()
    {
        (await SetCompletedAsync(9999, true)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // AC8

    [Fact]
    public async Task Delete_Returns204_ThenGetReturns404()
    {
        var created = await CreateAsync("delete me");

        (await _client.DeleteAsync($"/Todos/{created.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _client.GetAsync($"/Todos/{created.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_Returns404_ForUnknownId()
    {
        (await _client.DeleteAsync("/Todos/9999")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<Todo> CreateAsync(string title)
    {
        var response = await _client.PostAsJsonAsync("/Todos", new { title, isCompleted = false });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<Todo>())!;
    }

    private Task<HttpResponseMessage> SetCompletedAsync(int id, bool isCompleted) =>
        _client.PutAsJsonAsync($"/Todos/{id}/completed", new { isCompleted });
}
