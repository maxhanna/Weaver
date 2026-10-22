using System.Diagnostics;
using System.Reflection;
using Weaver.Services;
using Weaver.Hubs;

// UTF-8 so the welcome banner's emoji/arrows/box-drawing characters render on
// the Windows console instead of mojibake (also fixes em-dashes in log lines).
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

// Startup-log destination; assigned after the data-folder writability probe.
// Every console line mirrors into this file (see Log()).
string? _startupLogPath = null;

// `--self-check`: verify the bundled native libraries (SQLite + tree-sitter) load,
// then exit 0 on success / 1 on failure. The in-app updater runs a freshly
// downloaded Weaver.exe with this flag before replacing the running copy — a stale
// partial publish (natives not bundled inside the exe) fails here and is rejected
// instead of being shipped to users.
if (args.Contains("--self-check"))
{
    var sqliteOk = TryOpenSqlite(out var scSqliteVer, out _);
    var tsOk = TryTreeSitterCheck();
    if (sqliteOk && tsOk)
    {
        Console.WriteLine($"self-check OK — SQLite {scSqliteVer}, tree-sitter loaded");
        return 0;
    }
    Console.WriteLine("self-check FAILED — native libraries did not load");
    return 1;
}

WeaverLogo.DisplayLogo();
var builder = WebApplication.CreateBuilder(args);
var basePath = builder.Environment.ContentRootPath;
var assembly = Assembly.GetExecutingAssembly();

// Single-file publish bundles appsettings.json INSIDE Weaver.exe, so a lone exe
// copied to a fresh folder has no on-disk copy. Fall back to the embedded one so
// the Editor:* settings keep working (the on-disk file, when present, wins).
if (!File.Exists(Path.Combine(basePath, "appsettings.json")))
{
    var resName = assembly.GetManifestResourceNames()
        .FirstOrDefault(r => r.EndsWith("appsettings.json", StringComparison.OrdinalIgnoreCase));
    if (resName != null)
    {
        var res = assembly.GetManifestResourceStream(resName);
        if (res != null) builder.Configuration.AddJsonStream(res);
    }
}
var weaverDataDir = Path.Combine(basePath, "data");
var configPath = Path.Combine(basePath, "weaverconfig.json");

// Database lives in Weaver's data/ folder alongside other persisted data
var dbPath = Path.Combine(weaverDataDir, "weaver.db");

// True when this launch creates a brand-new database (e.g. the exe was copied to
// a fresh folder) — the welcome banner mentions it so first-time users know where
// their data will live. Captured before DatabaseService creates the file below.
var isFirstRun = !File.Exists(dbPath);

// Startup log: mirrors the console so a crash can be inspected afterwards. It
// lives next to the database when that folder is writable; otherwise it falls
// back to %LOCALAPPDATA%\Weaver\ so the warning about the unwritable folder is
// itself recorded.
var dataWritable = IsDirectoryWritable(weaverDataDir);
// %LOCALAPPDATA% is empty on headless Linux — fall back to the data dir for the log there.
var localAppDataDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
_startupLogPath = dataWritable || string.IsNullOrWhiteSpace(localAppDataDir)
    ? Path.Combine(weaverDataDir, "weaver-startup.log")
    : Path.Combine(localAppDataDir, "Weaver", "weaver-startup.log");
Log($"── Weaver startup ── {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
Log($"Working folder: {basePath}");
Log($"Data folder:    {weaverDataDir}");

// Windows protects folders like Program Files — the app must be able to create
// data/weaver.db next to the exe, so catch an unwritable folder up front with a
// clear message instead of an unhandled crash that flashes the console shut.
if (!dataWritable)
{
    Log("");
    Log("⚠  Weaver can't write to its data folder:");
    Log("");
    Log("    " + weaverDataDir);
    Log("");
    Log("Weaver stores its database (weaver.db) next to the exe, but this");
    Log("folder isn't writable — Windows protects folders like Program Files.");
    Log("");
    Log("Fix: move Weaver.exe to a writable folder, for example:");
    Log("");
    var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    Log("    " + (string.IsNullOrWhiteSpace(desktop)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop", "Weaver", "Weaver.exe")
        : Path.Combine(desktop, "Weaver", "Weaver.exe")));
    Log("");
    Log("Press any key to exit...");
    try { Console.ReadKey(true); } catch { }
    Environment.Exit(1);
}

// DatabaseService (central SQLite store — replaces all JSON file storage).
// Guarded separately: a corrupt/locked DB file would otherwise crash unlogged,
// which is exactly the failure this startup log exists to make inspectable.
// The catch always exits the process, so the null-forgiving init is safe.
DatabaseService dbService = null!;
try
{
    dbService = new DatabaseService(dbPath, weaverDataDir, configPath);
}
catch (Exception ex)
{
    Log("");
    Log("Weaver failed to initialize its database:");
    Log(ex.ToString());
    Log("");    Log("Press any key to exit...");
    try { Console.ReadKey(true); } catch { }
    Environment.Exit(1);
}

// Native-library self-check: a throwaway in-memory connection proves e_sqlite3
// actually loaded (the bundled natives extracted OK). A packaging regression
// would crash here — fail loudly with a clear message instead of dying mid-run.
if (!TryOpenSqlite(out var sqliteVer, out var sqliteErr))
{
    Log("");
    Log("Native libraries failed to load — Weaver cannot run:");
    Log($"   {sqliteErr}");
    Log("");
    Log("This usually means e_sqlite3.dll (or another native library) was not bundled");
    Log("inside Weaver.exe. Re-publish with the publish script and re-upload.");
    Log("");
    Log("Press any key to exit...");
    try { Console.ReadKey(true); } catch { }
    Environment.Exit(1);
}
Log($"Native libraries OK — SQLite {sqliteVer} loaded (e_sqlite3 extracted and ready).");

builder.Services.AddSingleton(dbService);

builder.Services.AddSingleton<TerminalService>();
builder.Services.AddSingleton<ConfigFileService>(sp => new ConfigFileService(sp.GetRequiredService<DatabaseService>()));
builder.Services.AddSingleton<EmailService>();
builder.Services.AddSingleton(new FileHintsManager(dbService));
builder.Services.AddSingleton(new CalendarService(dbService));
builder.Services.AddSingleton<ChangelogService>();
builder.Services.AddSingleton<GitService>();
builder.Services.AddSingleton<PushNotificationService>(sp => new PushNotificationService(dbService));
builder.Services.AddHttpClient("llama", client =>
{
    client.Timeout = TimeSpan.FromMinutes(30);
});
builder.Services.AddSingleton<AiServerDiscoveryService>();
builder.Services.AddSingleton<IContentEditHeuristic, ContentEditHeuristic>();
builder.Services.AddSingleton<IFormattingEditHeuristic, FormattingEditHeuristic>();
builder.Services.AddSingleton<IStructureEditHeuristic, StructureEditHeuristic>();
builder.Services.AddSingleton<IAnchorEditHeuristic, AnchorEditHeuristic>();
builder.Services.AddControllers();
builder.Services.AddSingleton<BoardDataService>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<BoardDataService>>();
    return new BoardDataService(dbService, logger);
});
builder.Services.AddSignalR();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
}));
var app = builder.Build();
app.UseRouting();
app.UseCors();

// Serve static files from wwwroot/ on disk with caching (fast path)
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.Append("Cache-Control", "public, max-age=3600");
    }
});

var resources = assembly.GetManifestResourceNames();
// Serve index.html at root (fallback to embedded resource)
app.MapGet("/", async context =>
{
    var indexRes = resources.First(r => r.EndsWith("wwwroot.index.html"));
    using var stream = assembly.GetManifestResourceStream(indexRes)!;
    using var reader = new StreamReader(stream);
    var html = await reader.ReadToEndAsync();
    context.Response.ContentType = "text/html";
    await context.Response.WriteAsync(html);
});
// Serve static files from embedded resources (fallback if not on disk)
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
        ".json" => "application/json",
        ".png" => "image/png",
        ".jpg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream"
    };
    await stream.CopyToAsync(context.Response.Body);
});
try
{
    app.MapControllers();
    app.MapHub<CoEditHub>("/hubs/coEdit");
}
catch (System.Reflection.ReflectionTypeLoadException ex)
{
    Log("Controller type-load failure — loader exceptions:");
    foreach (var le in ex.LoaderExceptions ?? Array.Empty<Exception>())
        Log("   " + le);
    throw;
}

// If another Weaver instance is already listening on the default port, fall back
// to the next free port instead of crashing — so a double-clicked exe always
// starts (e.g. when a second copy runs while the first is still open).
//
// Honor an explicit listen URL (`--urls` / `urls` config) FIRST: the WebHost's
// app.Urls does not reliably reflect a command-line `--urls` value, so read the
// resolved configuration directly — otherwise a launcher pointing Weaver at a
// specific free port (e.g. `--urls http://127.0.0.1:{port}`) is silently overridden
// back to the 5000 default and the launcher's port is never bound. The resolved
// URL is then bound explicitly so Kestrel listens exactly where we decided.
var listenUrl = (builder.Configuration["urls"] ?? app.Urls.FirstOrDefault() ?? "http://127.0.0.1:5000")
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .FirstOrDefault() ?? "http://127.0.0.1:5000";
var freeUrl = EnsureFreePort(listenUrl, out var originalPort);
if (freeUrl != listenUrl)
{
    Log($"Port {originalPort} is already in use — starting on {freeUrl} instead.");
}
app.Urls.Clear();
app.Urls.Add(freeUrl);

try
{
    var runTask = app.RunAsync();

    // Background-changelog refresh: fire-and-forget so the panel is ready
    // with fresh GitHub release data the first time a user opens it.
    _ = Task.Run(async () =>
    {
        try
        {
            var changelog = app.Services.GetRequiredService<ChangelogService>();
            await changelog.FetchChangelogAsync();
            Log("Changelog synced from GitHub.");
        }
        catch (Exception ex) { Log($"Changelog background sync failed: {ex.Message}"); }
    });

    // Now Kestrel has started and URLs are populated
    var url = app.Urls.FirstOrDefault() ?? freeUrl;
    PrintWelcomeBanner(url, basePath, isFirstRun);

    if (!args.Contains("--no-open-browser"))
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    await runTask;
}
catch (Exception ex)
{
    // A double-clicked console app would otherwise flash shut with the error —
    // keep the window open so the failure is actually visible (and log it).
    Log("");
    Log("Weaver failed to start:");
    Log(ex.ToString());
    Log("");
    Log("Press any key to exit...");
    try { Console.ReadKey(true); } catch { }
}

return 0;

// Returns a URL on a free port when the configured one is taken, otherwise the original.
static string EnsureFreePort(string url, out int originalPort)
{
    originalPort = 0;
    try
    {
        var uri = new Uri(url);
        originalPort = uri.Port;
        if (ServerLauncherService.IsPortFree(originalPort)) return url;
        var next = originalPort + 1;
        while (next < originalPort + 50 && !ServerLauncherService.IsPortFree(next)) next++;
        return $"http://127.0.0.1:{next}";
    }
    catch { return url; }
}

static bool IsDirectoryWritable(string dir)
{
    try
    {
        Directory.CreateDirectory(dir);
        var probe = Path.Combine(dir, ".weaver-write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(probe, "ok");
        File.Delete(probe);
        return true;
    }
    catch { return false; }
}

static bool TryOpenSqlite(out string version, out string? error)
{
    version = "unknown";
    error = null;
    try
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version();";
        version = cmd.ExecuteScalar()?.ToString() ?? "unknown";
        return true;
    }
    catch (Exception ex) { error = ex.Message; return false; }
}

// Proves the tree-sitter native parsers (tree-sitter.dll + tree-sitter-javascript.dll)
// load from the bundle — the other native family the app depends on for edits.
static bool TryTreeSitterCheck()
{
    try
    {
        using var language = new TreeSitter.Language("JavaScript");
        var parser = new TreeSitter.Parser(language);
        using var tree = parser.Parse("var x = 1;");
        return tree != null;
    }
    catch { return false; }
}

void PrintWelcomeBanner(string url, string basePath, bool firstRun)
{
    Log("");
    Log("═══════════════════════════════════════════════════════════");
    Log("   🕷️  Weaver is up and running!");
    Log("");
    Log("   → Open:   " + url);
    Log("   → Data:   " + Path.Combine(basePath, "data"));
    if (firstRun)
        Log("   → First run — welcome! Your data will be stored here.");
    Log("");
    Log("   Keep this window open — closing it stops Weaver.");
    Log("═══════════════════════════════════════════════════════════");
    Log("");
}

// Console + startup-log mirror. The log file is decided up front (data folder
// when writable, %LOCALAPPDATA%\Weaver otherwise) and rotated once it grows past
// 256 KB (previous launch kept as weaver-startup.prev.log).
void Log(string line)
{
    Console.WriteLine(line);
    if (_startupLogPath == null) return;
    try
    {
        var dir = Path.GetDirectoryName(_startupLogPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var fi = new FileInfo(_startupLogPath);
        if (fi.Exists && fi.Length > 256 * 1024)
        {
            var prev = _startupLogPath + ".prev.log";
            try { if (File.Exists(prev)) File.Delete(prev); File.Move(_startupLogPath, prev); } catch { }
        }
        File.AppendAllText(_startupLogPath, line + Environment.NewLine);
    }
    catch { /* logging is best-effort */ }
}