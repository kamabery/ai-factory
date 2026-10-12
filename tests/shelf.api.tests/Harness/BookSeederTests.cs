using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using shelf.api.tests.Infrastructure;
using shelf.data;

namespace shelf.api.tests.Harness;

/// <summary>
/// Covers <see cref="BookSeeder"/> directly, because its interesting branch is the one the
/// application's own startup almost never takes.
/// </summary>
/// <remarks>
/// Both containers use WithDataVolume, so rows outlive a restart and the seeder has to decide
/// whether what it finds is still usable. A fresh database exercises only the "empty" path; the
/// stale path is what runs on a machine that was last used a week ago, and it is the one that
/// deletes. Left untested it would be the branch nobody discovers is broken until a Monday.
/// </remarks>
[Trait(TestCategories.Category, TestCategories.Unit)]
public sealed class BookSeederTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private AppDbContext _context = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

        await _context.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task SeedAsync_OnAnEmptyDatabase_WritesFiveRows()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);

        await BookSeeder.SeedAsync(_context);

        var books = await _context.Books.OrderBy(book => book.Name).ToListAsync();

        books.ShouldSatisfyAllConditions(
            () => books.Count.ShouldBe(5),
            () => books.ShouldAllBe(book => book.Name.Length > 0),
            // The newest row is today's, which is exactly what the staleness check reads back.
            () => books.Max(book => book.UpdatedOn).ShouldBe(today));
    }

    [Fact]
    public async Task SeedAsync_WhenTheSeedIsCurrent_LeavesItAlone()
    {
        await BookSeeder.SeedAsync(_context);
        var original = await _context.Books.OrderBy(book => book.Name).ToListAsync();

        await BookSeeder.SeedAsync(_context);

        var after = await _context.Books.OrderBy(book => book.Name).ToListAsync();

        after.Select(book => (book.Id, book.Name, book.Quantity, book.UpdatedOn))
            .ShouldBe(original.Select(book => (book.Id, book.Name, book.Quantity, book.UpdatedOn)));
    }

    [Fact]
    public async Task SeedAsync_WhenTheSeedIsStale_ReplacesEveryRow()
    {
        // What a machine that was last run a fortnight ago looks like. This is the branch that
        // deletes, and the only test that exercises it.
        var today = DateOnly.FromDateTime(DateTime.Now);

        _context.Books.AddRange(Enumerable.Range(1, 5).Select(index => new Book
        {
            Name = $"Stale {index}",
            Quantity = 0,
            UpdatedOn = today.AddDays(-14)
        }));

        await _context.SaveChangesAsync();

        await BookSeeder.SeedAsync(_context);

        var books = await _context.Books.OrderBy(book => book.Name).ToListAsync();

        books.ShouldSatisfyAllConditions(
            () => books.Count.ShouldBe(5),
            () => books.ShouldNotContain(book => book.Name.StartsWith("Stale")),
            () => books.Max(book => book.UpdatedOn).ShouldBe(today));
    }
}
