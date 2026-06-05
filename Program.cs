using System.Diagnostics;
using Weaver.Services;
using Weaver.Hubs;
using System.Reflection;

// ── Self-update mode ──────────────────────────────────────────────
// Weaver.exe --update-self <tempExe> <originalExe>
if (args.Length >= 3 && args[0] == "--update-self")
{
    var newExe = args[1];
    var oldExe = args[2];
    await Task.Delay(2000);
    while (true)
    {
        try { File.Copy(newExe, oldExe, overwrite: true); break; }
        catch { await Task.Delay(500); }
    }
    try { File.Delete(newExe); } catch { }
    Process.Start(oldExe);
    return;
}

WeaverLogo.DisplayLogo();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<TerminalService>();
builder.Services.AddSingleton<ConfigFileService>();
builder.Services.AddSingleton<EmailService>();

var basePath = builder.Environment.ContentRootPath;
builder.Services.AddSingleton(new FileHintsManager(basePath));
builder.Services.AddSingleton(new Weaver.Services.BoardDataService(basePath));
builder.Services.AddSingleton(new CalendarService(basePath));
builder.Services.AddSingleton<GitService>();

builder.Services.AddHttpClient("llama", client =>
{
    client.Timeout = TimeSpan.FromMinutes(30);
});
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
}));

var app = builder.Build();
 
app.UseRouting();
app.UseCors();
var assembly = Assembly.GetExecutingAssembly();
var resources = assembly.GetManifestResourceNames();

// Serve index.html at root
app.MapGet("/", async context =>
{
    var indexRes = resources.First(r => r.EndsWith("wwwroot.index.html"));
    using var stream = assembly.GetManifestResourceStream(indexRes)!;
    using var reader = new StreamReader(stream);
    var html = await reader.ReadToEndAsync();

    context.Response.ContentType = "text/html";
    await context.Response.WriteAsync(html);
});

// Serve ANY embedded static file
app.MapGet("/{**path}", async context =>
{
    string path = context.Request.Path.Value!.TrimStart('/').Replace("/", ".");
    string? resourceName = resources.FirstOrDefault(r => r.EndsWith(path));

    if (resourceName == null)
    {
        context.Response.StatusCode = 404;
        return;
    }

    using var stream = assembly.GetManifestResourceStream(resourceName)!;

    context.Response.ContentType = Path.GetExtension(path) switch
    {
        ".js" => "application/javascript",
        ".css" => "text/css",
        ".html" => "text/html",
        ".png" => "image/png",
        ".jpg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream"
    };

    await stream.CopyToAsync(context.Response.Body);
});

app.MapControllers();
app.MapHub<CoEditHub>("/hubs/coEdit"); 

app.Run();
