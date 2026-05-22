using MaestroBackend.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<TerminalService>();
builder.Services.AddHttpClient("llama");
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
