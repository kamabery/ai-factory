// The job runner (roadmap spec 07 ports runner.ps1 here). Empty until then.
var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.Build().Run();
