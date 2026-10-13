using Microsoft.AspNetCore.Mvc;
using shelf.data;
using shelf.data.Repositories;

namespace shelf.api.Controllers;

/// <summary>
/// CRUD and check-off for <see cref="Todo"/>.
/// </summary>
/// <remarks>
/// <para>The entity is also the wire shape, so the body's <c>id</c> is never trusted: create
/// zeroes it so the database assigns one, and update copies onto the row the route id loaded.</para>
/// <para>Update and check-off modify the tracked instance <see cref="IRepository{T}.GetByIdAsync"/>
/// returned rather than passing a second instance with the same key to UpdateAsync, which EF
/// would refuse to attach.</para>
/// </remarks>
[ApiController]
[Route("[controller]")]
public class TodosController(IRepository<Todo> todos) : ControllerBase
{
    [HttpGet(Name = "GetTodos")]
    [EndpointSummary("List every todo")]
    [ProducesResponseType<IEnumerable<Todo>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<Todo>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await todos.GetAllAsync(cancellationToken));

    [HttpGet("{id:int}", Name = "GetTodo")]
    [EndpointSummary("Get one todo")]
    [ProducesResponseType<Todo>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Todo>> GetById(int id, CancellationToken cancellationToken) =>
        await todos.GetByIdAsync(id, cancellationToken) is { } todo ? Ok(todo) : NotFound();

    [HttpPost(Name = "CreateTodo")]
    [EndpointSummary("Create a todo")]
    [ProducesResponseType<Todo>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Todo>> Create(Todo todo, CancellationToken cancellationToken)
    {
        if (!IsValidTitle(todo.Title))
        {
            return ValidationProblem(ModelState);
        }

        var created = await todos.AddAsync(
            new Todo { Title = todo.Title, IsCompleted = todo.IsCompleted },
            cancellationToken);

        return CreatedAtRoute("GetTodo", new { id = created.Id }, created);
    }

    [HttpPut("{id:int}", Name = "UpdateTodo")]
    [EndpointSummary("Replace a todo's title and completed flag")]
    [ProducesResponseType<Todo>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Todo>> Update(int id, Todo todo, CancellationToken cancellationToken)
    {
        if (!IsValidTitle(todo.Title))
        {
            return ValidationProblem(ModelState);
        }

        if (await todos.GetByIdAsync(id, cancellationToken) is not { } existing)
        {
            return NotFound();
        }

        existing.Title = todo.Title;
        existing.IsCompleted = todo.IsCompleted;
        await todos.UpdateAsync(existing, cancellationToken);

        return Ok(existing);
    }

    [HttpPut("{id:int}/completed", Name = "SetTodoCompleted")]
    [EndpointSummary("Check a todo off, or un-check it")]
    [ProducesResponseType<Todo>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Todo>> SetCompleted(
        int id, TodoCompletion completion, CancellationToken cancellationToken)
    {
        if (await todos.GetByIdAsync(id, cancellationToken) is not { } existing)
        {
            return NotFound();
        }

        existing.IsCompleted = completion.IsCompleted;
        await todos.UpdateAsync(existing, cancellationToken);

        return Ok(existing);
    }

    [HttpDelete("{id:int}", Name = "DeleteTodo")]
    [EndpointSummary("Delete a todo")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        // EfRepository.DeleteAsync is silent on a missing row, so check first to return 404.
        if (await todos.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFound();
        }

        await todos.DeleteAsync(id, cancellationToken);

        return NoContent();
    }

    // Explicit, because `required` accepts "" and SQLite does not enforce HasMaxLength.
    private bool IsValidTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            ModelState.AddModelError(nameof(Todo.Title), "Title must not be blank.");
        }
        else if (title.Length > Todo.TitleMaxLength)
        {
            ModelState.AddModelError(
                nameof(Todo.Title), $"Title must be at most {Todo.TitleMaxLength} characters.");
        }

        return ModelState.IsValid;
    }
}
