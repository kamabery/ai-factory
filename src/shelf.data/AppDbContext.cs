using Microsoft.EntityFrameworkCore;

namespace shelf.data;

/// <summary>
/// The one DbContext. Every provider maps the same model; only the migrations differ.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Book> Books => Set<Book>();

    public DbSet<Todo> Todos => Set<Todo>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Book>(book =>
        {
            book.HasKey(w => w.Id);

            // Unique rather than merely indexed: the name is the human-facing identity here, and
            // BookSeeder's replace-all pass would otherwise be free to leave two rows that read
            // identically in the UI.
            book.HasIndex(w => w.Name).IsUnique();

            book.Property(w => w.Name).HasMaxLength(64);
        });

        modelBuilder.Entity<Todo>(todo =>
        {
            todo.HasKey(t => t.Id);

            todo.Property(t => t.Title).HasMaxLength(Todo.TitleMaxLength);
        });
    }
}
