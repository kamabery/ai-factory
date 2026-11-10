// The dashboard server (roadmap spec 08 ports serve.ps1 here). Empty until then.
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

var app = builder.Build();
app.MapDefaultEndpoints();
app.Run();
