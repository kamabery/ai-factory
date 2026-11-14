using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Data.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Factory.Data;

/// <summary>
/// The board and events on EF Core (contract, "State backends", Database): reads hold no lock, and each save is one
/// transaction whose every row write is checked against the version the command read.
/// </summary>
public sealed class EfBoardRows(FactoryDbs dbs) : IBoardRows
{
    private string T => dbs.TenantId;

    public BoardRead? Read()
    {
        using var db = dbs.Open();
        // Every commit bumps the revision: the same revision before and after the reads means they saw one state.
        for (var attempt = 0; ; attempt++)
        {
            var b = db.Boards.AsNoTracking().FirstOrDefault(x => x.TenantId == T);
            if (b is null) return null;
            var read = Load(db, b);
            if (attempt == 4 || db.Boards.Where(x => x.TenantId == T).Select(x => x.Revision).First() == b.Revision) return read;
        }
    }

    private BoardRead Load(FactoryDb db, BoardRow b)
    {
        var ideas = db.Ideas.AsNoTracking().Where(x => x.TenantId == T).OrderBy(x => x.Ordinal).ThenBy(x => x.Id).ToList();
        var jobs = db.Jobs.AsNoTracking().Where(x => x.TenantId == T).OrderBy(x => x.Ordinal).ThenBy(x => x.Id).ToList();
        var board = BoardRows.Assemble(b.Skeleton, ideas.Select(i => i.Json), jobs.Select(j => j.Json));
        return new BoardRead(board, b.Skeleton, b.Version,
            ideas.ToDictionary(i => i.Id, i => new RowRead(i.Json, i.Version, i.Ordinal), StringComparer.OrdinalIgnoreCase),
            jobs.ToDictionary(j => j.Id, j => new RowRead(j.Json, j.Version, j.Ordinal), StringComparer.OrdinalIgnoreCase));
    }

    public BoardRead Save(BoardRead read, JsonObject board)
    {
        var plan = BoardRows.Plan(read, board);
        using var db = dbs.Open();
        using var tx = db.Database.BeginTransaction();
        var version = read.Version;
        if (read.Version == 0)
        {
            if (db.Boards.Any(x => x.TenantId == T)) throw Conflict("the board");
            db.Boards.Add(new BoardRow { TenantId = T, Skeleton = plan.Skeleton, Revision = 0, Version = 1 });
            Commit(db, "the board");
            version = 1;
        }
        else if (plan.SkeletonChanged)
        {
            var n = db.Boards.Where(x => x.TenantId == T && x.Version == read.Version)
                .ExecuteUpdate(s => s.SetProperty(x => x.Skeleton, plan.Skeleton).SetProperty(x => x.Version, read.Version + 1));
            if (n == 0) throw Conflict("the board");
            version = read.Version + 1;
        }
        foreach (var c in plan.Rows) Write(db, c);
        Bump(db);
        tx.Commit();

        var ideas = new Dictionary<string, RowRead>(read.Ideas, StringComparer.OrdinalIgnoreCase);
        var jobs = new Dictionary<string, RowRead>(read.Jobs, StringComparer.OrdinalIgnoreCase);
        foreach (var c in plan.Rows)
        {
            var rows = c.Kind == RowKind.Idea ? ideas : jobs;
            if (c.Change == ChangeKind.Delete) rows.Remove(c.Id);
            else rows[c.Id] = new RowRead(c.Json!, c.Change == ChangeKind.Insert ? 1 : c.ReadVersion + 1, c.Ordinal);
        }
        return new BoardRead(board, plan.Skeleton, version, ideas, jobs);
    }

    private void Write(FactoryDb db, RowChange c)
    {
        var n = (c.Kind, c.Change) switch
        {
            (RowKind.Idea, ChangeKind.Insert) => Insert(db, NewIdea(c), c.Id),
            (RowKind.Job, ChangeKind.Insert) => Insert(db, NewJob(c), c.Id),
            (RowKind.Idea, ChangeKind.Update) => UpdateIdea(db, c),
            (RowKind.Job, ChangeKind.Update) => UpdateJob(db, c),
            (RowKind.Idea, _) => db.Ideas.Where(x => x.TenantId == T && x.Id == c.Id && x.Version == c.ReadVersion).ExecuteDelete(),
            _ => db.Jobs.Where(x => x.TenantId == T && x.Id == c.Id && x.Version == c.ReadVersion).ExecuteDelete(),
        };
        if (n == 0) throw Conflict(c.Id);
    }

    private int UpdateIdea(FactoryDb db, RowChange c)
    {
        var i = new Idea(c.Node!);
        var (status, target) = (i.Status, i.Target);
        return db.Ideas.Where(x => x.TenantId == T && x.Id == c.Id && x.Version == c.ReadVersion).ExecuteUpdate(s => s
            .SetProperty(x => x.Json, c.Json!).SetProperty(x => x.Ordinal, c.Ordinal).SetProperty(x => x.Status, status)
            .SetProperty(x => x.Target, target).SetProperty(x => x.Version, c.ReadVersion + 1));
    }

    private int UpdateJob(FactoryDb db, RowChange c)
    {
        var row = NewJob(c);
        return db.Jobs.Where(x => x.TenantId == T && x.Id == c.Id && x.Version == c.ReadVersion).ExecuteUpdate(s => s
            .SetProperty(x => x.Json, row.Json).SetProperty(x => x.Ordinal, row.Ordinal).SetProperty(x => x.Idea, row.Idea)
            .SetProperty(x => x.Stage, row.Stage).SetProperty(x => x.Round, row.Round).SetProperty(x => x.Target, row.Target)
            .SetProperty(x => x.Kind, row.Kind).SetProperty(x => x.Version, c.ReadVersion + 1));
    }

    private IdeaRow NewIdea(RowChange c)
    {
        var i = new Idea(c.Node!);
        return new IdeaRow { TenantId = T, Id = c.Id, Ordinal = c.Ordinal, Status = i.Status, Target = i.Target, Json = c.Json!, Version = 1 };
    }

    private JobRow NewJob(RowChange c)
    {
        var j = new Job(c.Node!);
        return new JobRow
        {
            TenantId = T, Id = c.Id, Ordinal = c.Ordinal, Idea = j.Idea, Stage = PsJsonReader.Str(c.Node!["stage"]),
            Round = c.Node!["round"] is null ? 0 : j.Round, Target = j.Target, Kind = j.Kind, Json = c.Json!, Version = 1,
        };
    }

    private static int Insert(FactoryDb db, object row, string id)
    {
        db.Add(row);
        Commit(db, id);
        return 1;
    }

    // A key another command inserted first is a conflict like a version that moved.
    private static void Commit(FactoryDb db, string what)
    {
        try { db.SaveChanges(); }
        catch (DbUpdateException) { throw Conflict(what); }
        db.ChangeTracker.Clear();
    }

    private static FactoryException Conflict(string what) => new($"board conflict: {what} changed since this command read it; nothing was saved");

    private void Bump(FactoryDb db) => db.Boards.Where(x => x.TenantId == T).ExecuteUpdate(s => s.SetProperty(x => x.Revision, x => x.Revision + 1));

    public void AddEvent(string line)
    {
        using var db = dbs.Open();
        using var tx = db.Database.BeginTransaction();
        // An event before any board starts the board, so the revision has a row to count in.
        if (!db.Boards.Any(x => x.TenantId == T))
            db.Boards.Add(new BoardRow { TenantId = T, Skeleton = BoardRows.Skeleton(Board.Empty()), Revision = 0, Version = 1 });
        db.Events.Add(NewEvent(line));
        db.SaveChanges();
        Bump(db);
        tx.Commit();
    }

    private EventRow NewEvent(string line)
    {
        var c = BoardRows.Columns(line);
        return new EventRow { TenantId = T, Ts = c.Ts, Job = c.Job, Idea = c.Idea, Kind = c.Kind, Line = line };
    }

    public long Revision()
    {
        using var db = dbs.Open();
        return db.Boards.Where(x => x.TenantId == T).Select(x => (long?)x.Revision).FirstOrDefault() ?? 0;
    }

    public IReadOnlyList<EventLine> EventsAfter(long seq)
    {
        using var db = dbs.Open();
        return [.. db.Events.AsNoTracking().Where(x => x.TenantId == T && x.Seq > seq).OrderBy(x => x.Seq).Select(x => new EventLine(x.Seq, x.Line))];
    }

    public ImportResult Import(JsonObject board, IReadOnlyList<string> events)
    {
        var plan = BoardRows.Plan(BoardRead.None(), board);
        using var db = dbs.Open();
        using var tx = db.Database.BeginTransaction();
        if (db.Boards.Any(x => x.TenantId == T)) throw new FactoryException("state-import refused: the database already has a board");
        db.Boards.Add(new BoardRow { TenantId = T, Skeleton = plan.Skeleton, Revision = 1, Version = 1 });
        foreach (var c in plan.Rows) db.Add(c.Kind == RowKind.Idea ? NewIdea(c) : NewJob(c));
        db.SaveChanges();
        db.ChangeTracker.Clear();
        // One insert at a time: a batched identity insert (SQL Server's MERGE) need not number rows in order.
        foreach (var line in events)
        {
            db.Events.Add(NewEvent(line));
            db.SaveChanges();
            db.ChangeTracker.Clear();
        }

        var back = Load(db, db.Boards.AsNoTracking().First(x => x.TenantId == T));
        if (!JsonNode.DeepEquals(board, back.Board))
            throw new FactoryException("state-import refused: the board read back from the database differs from board.json; nothing was imported");
        var lines = db.Events.AsNoTracking().Where(x => x.TenantId == T).OrderBy(x => x.Seq).Select(x => x.Line).ToList();
        if (!lines.SequenceEqual(events, StringComparer.Ordinal))
            throw new FactoryException("state-import refused: the events read back from the database differ from events.jsonl; nothing was imported");
        tx.Commit();
        return new ImportResult(plan.Rows.Count(r => r.Kind == RowKind.Idea), plan.Rows.Count(r => r.Kind == RowKind.Job), events.Count);
    }
}
