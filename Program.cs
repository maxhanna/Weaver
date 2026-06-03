using MaestroBackend.Services;
using MaestroBackend;

MaestroLogo.DisplayLogo();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<TerminalService>();
builder.Services.AddSingleton<ConfigFileService>();
builder.Services.AddSingleton<EmailService>();

var basePath = builder.Environment.ContentRootPath;
builder.Services.AddSingleton(new FileHintsManager(basePath));
builder.Services.AddSingleton(new MaestroBackend.Services.BoardDataService(basePath));
builder.Services.AddSingleton(new CalendarService(basePath));
builder.Services.AddSingleton<GitService>();

builder.Services.AddHttpClient("llama", client =>
{
    client.Timeout = TimeSpan.FromMinutes(30);
});
builder.Services.AddControllers();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
}));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseCors();

app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();
