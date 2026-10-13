namespace shelf.data;

/// <summary>
/// A todo, as it is both stored and served. A title and a completed flag, nothing else.
/// </summary>
/// <remarks>
/// Like <see cref="Book"/>, one type is both the entity and the wire shape, so <see cref="Id"/>
/// is on the wire. TodosController ignores a client-supplied id on create and lets the route id
/// win on update, so the body cannot pick or overwrite a row.
/// </remarks>
public class Todo
{
    /// <summary>The longest title the API accepts. Enforced by TodosController, because SQLite
    /// does not enforce HasMaxLength.</summary>
    public const int TitleMaxLength = 200;

    /// <summary>Database-generated, so deliberately not <c>required</c>.</summary>
    public int Id { get; set; }

    public required string Title { get; set; }

    public required bool IsCompleted { get; set; }
}

/// <summary>
/// The body of the check-off call: sets only <see cref="Todo.IsCompleted"/>.
/// </summary>
public class TodoCompletion
{
    public required bool IsCompleted { get; set; }
}
