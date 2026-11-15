using System.Text.Json.Nodes;
using Factory.Server;
using Factory.Server.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

public class ServerReadsTests : ServerTestBase
{
    private ServerSettings RealSettings(params (string Name, string? Value)[] env) =>
        new(Microsoft.Extensions.Options.Options.Create(new ServerOptions
        {
            EnvFile = Options.EnvFile,
            Environment = env.ToDictionary(e => e.Name, e => e.Value),
        }), Files);

    [Fact]
    public void The_run_mode_is_local_by_default_the_environment_wins_over_env_and_a_bad_one_is_null()
    {
        Assert.Equal("local", RealSettings().Mode);

        Files.Put(Options.EnvFile, "FACTORY_MODE=cloud\n");
        Assert.Equal("cloud", RealSettings().Mode);
        Assert.Equal("local", RealSettings(("FACTORY_MODE", "LOCAL")).Mode);
        Assert.Null(RealSettings(("FACTORY_MODE", "openrouter")).Mode);
    }

    [Fact]
    public void Idea_sessions_need_nothing_locally_and_the_foundry_resource_in_cloud_mode()
    {
        Assert.Null(RealSettings().IdeaBlocker);

        Assert.Equal("no FACTORY_FOUNDRY_RESOURCE (cloud mode)", RealSettings(("FACTORY_MODE", "cloud")).IdeaBlocker);
        Assert.Null(RealSettings(("FACTORY_MODE", "cloud"), ("FACTORY_FOUNDRY_RESOURCE", "fdy")).IdeaBlocker);
        Assert.Equal("FACTORY_MODE must be local or cloud", RealSettings(("FACTORY_MODE", "x")).IdeaBlocker);
    }

    [Fact]
    public void The_env_file_is_read_afresh_so_an_edit_shows_at_once()
    {
        var settings = RealSettings();
        Assert.Equal("claude-opus-5-5", settings.IdeaModel);

        Files.Put(Options.EnvFile, "FACTORY_IDEA_MODEL=\"claude-sonnet-5-5\"\nGITHUB_OWNER=\n");

        Assert.Equal("claude-sonnet-5-5", settings.IdeaModel);
        Assert.Null(settings.Env("GITHUB_OWNER"));
    }

    [Fact]
    public void Ideate_config_names_mode_provider_owner_and_deploys_and_never_a_key()
    {
        Settings.Env("GITHUB_OWNER").Returns("kamabery");
        Settings.Env("HCP_ORG").Returns("org");
        Settings.Env("ANTHROPIC_API_KEY").Returns("sk-secret");
        Settings.Mode.Returns("cloud");
        Settings.IdeaBlocker.Returns((string?)null);

        var body = Reads.IdeateConfig().Body;

        Assert.Equal("""{"enabled":true,"mode":"cloud","provider":"foundry","model":"claude-opus-5-5","owner":"kamabery","templatesRepo":null,"deploys":true}""", body);
        Assert.DoesNotContain("sk-secret", body);
    }

    [Fact]
    public void An_invalid_mode_has_no_provider()
    {
        Settings.Mode.Returns((string?)null);
        Settings.IdeaBlocker.Returns("FACTORY_MODE must be local or cloud");

        var config = JsonNode.Parse(Reads.IdeateConfig().Body)!;

        Assert.Equal((false, null, null), ((bool)config["enabled"]!, (string?)config["mode"], (string?)config["provider"]));
    }

    [Fact]
    public void Evals_and_lessons_list_every_job_file_newest_job_first_and_skip_the_rest()
    {
        Files.Put(Path.Combine(Home, "evals", "J-002.json"), """{"id":"J-002"}""");
        Files.Put(Path.Combine(Home, "evals", "J-010.json"), """{"id":"J-010"}""");
        Files.Put(Path.Combine(Home, "evals", "J-002.md"), "md");
        Files.Put(Path.Combine(Home, "evals", "notes.json"), "{}");
        Files.Put(Path.Combine(Home, "lessons", "J-001.json"), """{"id":"J-001"}""");

        Assert.Equal("""[{"id":"J-010"},{"id":"J-002"}]""", Reads.Evals().Body);
        Assert.Equal("""[{"id":"J-001"}]""", Reads.Lessons().Body);
        Assert.Equal("[]", new ReadsApi(Factory, Paths, new MemoryFiles(), Settings, Procs).Evals().Body);
    }

    [Fact]
    public void A_lesson_mark_passes_the_session_as_detail_only_when_there_is_one()
    {
        Reads.MarkLesson("J-001", """{"status":"proposed","session":"S-004"}""");
        Reads.MarkLesson("J-002", """{"status":"applied"}""");

        Factory.Received().Run("lesson-mark", "J-001", "proposed", "-Detail", "S-004");
        Factory.Received().Run("lesson-mark", "J-002", "applied");
    }

    [Fact]
    public void Feedback_goes_through_a_temp_file_that_is_gone_afterwards()
    {
        string? seen = null;
        Factory.Run(Arg.Any<string[]>()).Returns(c => { var a = c.Arg<string[]>(); seen = File.ReadAllText(a[3]); return CallResult.Done("ok"); });

        var r = Reads.Feedback("J-003", "Rating: good\n\nnice");

        Assert.Equal(("""{"ok":true,"message":"feedback saved"}""", "Rating: good\n\nnice"), (r.Body, seen));
        Assert.False(File.Exists(Path.Combine(Home, "feedback-4242.md")));
    }

    [Fact]
    public void Repos_is_list_repos_json_as_is_or_its_refusal()
    {
        Factory.Run("list-repos").Returns(CallResult.Done("""["a/b"]"""));
        Assert.Equal((200, """["a/b"]"""), (Reads.Repos().Status, Reads.Repos().Body));

        Factory.Run("list-repos").Returns(CallResult.Refuse("no GitHub App is set up; run factory init"));
        Assert.Equal(409, Reads.Repos().Status);
    }

    [Fact]
    public void Sessions_lists_every_open_session_with_its_summary_fields_only()
    {
        Files.Put(Path.Combine(Home, "sessions", "S-002", "session.json"), """{"id":"S-002","status":"idle","target":"a/b","text":"x","pending":{"id":"t"}}""");
        Files.Put(Path.Combine(Home, "sessions", "S-001", "session.json"), """{"id":"S-001","status":"closed"}""");
        Files.Put(Path.Combine(Home, "sessions", "S-003", "chat.json"), "{}");

        Assert.Equal("""[{"id":"S-002","target":"a/b","text":"x","status":"idle","idea":null,"bootstrap":null,"costUsd":null,"newTarget":null}]""", Reads.Sessions().Body);
    }

    [Fact]
    public void Purge_and_abandon_are_one_call_each()
    {
        Reads.Purge();
        Reads.AbandonIdea("I-004");

        Factory.Received().Run("purge");
        Factory.Received().Run("idea-abandon", "I-004");
    }

    [Fact]
    public void State_files_never_leave_the_state_dir_and_the_board_and_events_refresh_first()
    {
        var files = new StaticFiles(Paths, Cache, Opts);

        Assert.Equal(Path.Combine(Home, "jobs", "J-001", "run-1.jsonl"), files.State("jobs/J-001/run-1.jsonl").FilePath);
        Assert.Equal(400, files.State("../outside.txt").Status);
        files.State("board.json");
        files.State("events.jsonl");

        Cache.Received(1).Board();
        Cache.Received(1).Events();
    }

    [Fact]
    public void Vendor_files_and_the_index_come_from_the_dashboard_folder()
    {
        Options.DashboardDir = Path.Combine(Home, "dashboard");
        var files = new StaticFiles(Paths, Cache, Opts);

        Assert.Equal(Path.Combine(Home, "dashboard", "index.html"), files.Index().FilePath);
        Assert.Equal(Path.Combine(Home, "dashboard", "vendor", "a.js"), files.Vendor("a.js").FilePath);
        Assert.Equal(400, files.Vendor("../index.html").Status);
    }
}
