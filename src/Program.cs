using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OsuCollectionManager.Osu;
using OsuCollectionManager.Services;

// The web page (src/wwwroot) is served as static files. A published copy keeps wwwroot right next to the exe; while
// developing it is a few folders above the build output (src/bin/Debug/...). appsettings.json is read from the exe's
// folder too, so a shortcut or another working directory doesn't matter.
string appFolder = AppContext.BaseDirectory;
string webRoot = FindWebRoot(appFolder)
    ?? throw new DirectoryNotFoundException("The wwwroot folder was not found. It must sit next to the executable (or in the src folder).");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = appFolder,
    WebRootPath = webRoot,
});

// The app is for one person on one PC: the first Url is the address of "the app", used below to open the browser.
string appUrl = (builder.Configuration["Urls"] ?? "http://localhost:5000").Split(';')[0].Replace("*", "localhost").Replace("+", "localhost");

// Only one copy per port. A second launch just opens the browser tab of the copy that is already running.
using var singleInstance = new Mutex(true, $"Local\\OsuCollectionManager-{new Uri(appUrl).Port}", out bool isFirstInstance);
if (!isFirstInstance)
{
    Console.WriteLine($"osu! Collection Manager is already running, opening {appUrl}");
    OpenBrowser(appUrl);
    return;
}

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHttpClient("osuweb", c =>
{
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
    c.Timeout = TimeSpan.FromSeconds(30);
}).AddHttpMessageHandler<RateLimitHandler>();
builder.Services.AddHttpClient("mirror", c =>
{
    c.DefaultRequestHeaders.UserAgent.ParseAdd("osu_collection_manager/1.0 (+personal tool)");
    c.Timeout = TimeSpan.FromMinutes(5);
}).AddHttpMessageHandler<RateLimitHandler>();
builder.Services.AddTransient<RateLimitHandler>();
builder.Services.AddSingleton<UserSettingsStore>();
builder.Services.AddSingleton<OsuInstall>();
builder.Services.AddSingleton<OsuWebClient>();
builder.Services.AddSingleton<MirrorClient>();
builder.Services.AddSingleton<TrainingPlanner>();
builder.Services.AddSingleton<CollectionImporter>();
builder.Services.AddSingleton<JobManager>();

var app = builder.Build();

// Turn the "osu! is running" guard, a missing osu! folder and upstream failures into readable error responses.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (OsuNotConfiguredException e)
    {
        await WriteSetupRequired(ctx, e.Message);
    }
    catch (InvalidOperationException e)
    {
        ctx.Response.StatusCode = StatusCodes.Status409Conflict;
        await ctx.Response.WriteAsJsonAsync(new { error = e.Message });
    }
    catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException
                              && ctx.RequestServices.GetRequiredService<OsuInstall>().IsFolderMissing)
    {
        // The chosen osu! folder disappeared (drive unplugged, osu! uninstalled): ask for it again.
        await WriteSetupRequired(ctx, "The osu! folder can no longer be found. Please choose it again.");
    }
    catch (HttpRequestException e)
    {
        bool limited = e.StatusCode == System.Net.HttpStatusCode.TooManyRequests;
        ctx.Response.StatusCode = limited ? StatusCodes.Status429TooManyRequests : StatusCodes.Status502BadGateway;
        await ctx.Response.WriteAsJsonAsync(new
        {
            error = limited ? "The remote site is rate limiting requests (429). Wait a minute and try again." : e.Message,
        });
    }
});

// This server only listens on localhost, but any website you visit could still send requests to it.
// - Browsers attach an Origin header to cross-site writes, so refuse writes that come from another site.
// - A page that tricks the browser into resolving its own domain to 127.0.0.1 ("DNS rebinding") arrives with that
//   domain as Host, so only answer API requests addressed to localhost itself.
app.Use(async (ctx, next) =>
{
    bool isApi = ctx.Request.Path.StartsWithSegments("/api");
    string host = ctx.Request.Host.Host;
    if (isApi && !(host is "localhost" or "127.0.0.1" or "[::1]" or "::1"))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsJsonAsync(new { error = "Open the app through http://localhost." });
        return;
    }

    bool isWrite = ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(ctx.Request.Method);
    string origin = ctx.Request.Headers.Origin.ToString();
    bool foreign = origin.Length > 0
        && !(Uri.TryCreate(origin, UriKind.Absolute, out var uri) && string.Equals(uri.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase));
    if (isWrite && foreign)
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsJsonAsync(new { error = "Requests from other websites are not allowed." });
        return;
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/status", (OsuInstall osu) =>
{
    if (!osu.IsConfigured) return Results.Ok(new { configured = false });

    var db = osu.Database;
    return Results.Ok(new
    {
        configured = true,
        osuPath = osu.Root,
        songsPath = osu.SongsPath,
        player = db.PlayerName,
        dbVersion = db.Version,
        beatmaps = db.Beatmaps.Count,
        sets = db.Beatmaps.Select(b => b.SetId).Distinct().Count(),
        pendingImports = osu.PendingImports().Count,
        collections = osu.ReadCollections().Collections.Count,
        gameRunning = OsuInstall.IsGameRunning,
    });
});

// ---- First-run setup: where is the osu! folder? ---------------------------------------------

api.MapGet("/setup", (OsuInstall osu) => new { configured = osu.IsConfigured, path = osu.Root, candidates = osu.FindCandidates() });

api.MapPost("/setup", (OsuInstall osu, SetupRequest req) =>
{
    try { return Results.Ok(new { path = osu.SetRoot(req.Path) }); }
    catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
});

// Folder browser for the setup screen. (A native Windows dialog opened by the server ends up behind the browser
// window, where nobody sees it, so the browsing happens inside the page instead.)
api.MapGet("/setup/folders", (string? path) =>
{
    try { return Results.Ok(OsuLocator.ListFolders(path)); }
    catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
    {
        return Results.BadRequest(new { error = e.Message });
    }
});

// ---- Local library -------------------------------------------------------------------------

api.MapGet("/library", (OsuInstall osu, string? q, double? minStars, double? maxStars, GameMode? mode,
    string? sort, int page = 1, int pageSize = 50) =>
{
    IEnumerable<LocalBeatmap> maps = osu.Database.Beatmaps;
    if (mode is { } m) maps = maps.Where(b => b.Mode == m);
    if (minStars is { } lo) maps = maps.Where(b => b.StarRating >= lo);
    if (maxStars is { } hi) maps = maps.Where(b => b.StarRating <= hi);
    if (!string.IsNullOrWhiteSpace(q))
    {
        var terms = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        maps = maps.Where(b => terms.All(t =>
            $"{b.Artist} {b.Title} {b.Creator} {b.Version} {b.BeatmapId} {b.SetId}".Contains(t, StringComparison.OrdinalIgnoreCase)));
    }
    maps = sort switch
    {
        "stars" => maps.OrderByDescending(b => b.StarRating),
        "bpm" => maps.OrderByDescending(b => b.MaxBpm),
        "length" => maps.OrderByDescending(b => b.DrainSeconds),
        "played" => maps.OrderByDescending(b => b.LastPlayed ?? DateTime.MinValue),
        _ => maps.OrderBy(b => b.Artist).ThenBy(b => b.Title).ThenBy(b => b.StarRating),
    };
    var list = maps.ToList();
    return new { total = list.Count, page, items = list.Skip((page - 1) * pageSize).Take(pageSize) };
});

// Background image of an installed difficulty, read from its .osu [Events] section.
api.MapGet("/library/{md5}/background", (OsuInstall osu, string md5) =>
{
    if (osu.FindByMd5(md5) is not { } b) return Results.NotFound();
    string dir = Path.GetFullPath(Path.Combine(osu.SongsPath, b.Folder));
    string osuFile = Path.Combine(dir, b.FileName);
    if (!File.Exists(osuFile)) return Results.NotFound();
    foreach (var line in File.ReadLines(osuFile))
    {
        var m = Regex.Match(line, "^0,0,\"([^\"]+)\"");
        if (!m.Success) continue;
        string img = Path.GetFullPath(Path.Combine(dir, m.Groups[1].Value));
        if (!img.StartsWith(dir, StringComparison.OrdinalIgnoreCase) || !File.Exists(img)) break;
        string type = Path.GetExtension(img).ToLowerInvariant() is ".png" ? "image/png" : "image/jpeg";
        return Results.File(img, type);
    }
    return Results.NotFound();
});

// ---- Collections ---------------------------------------------------------------------------
// Names go in the query string/body because collection names can contain '/' and other characters.

api.MapGet("/collections", (OsuInstall osu) =>
    osu.ReadCollections().Collections.Select(c => new
    {
        c.Name,
        count = c.Hashes.Count,
        missing = c.Hashes.Count(h => osu.FindByMd5(h) is null),
    }));

api.MapGet("/collection", (OsuInstall osu, string name) =>
{
    var c = osu.ReadCollections().Collections.FirstOrDefault(x => x.Name == name);
    if (c is null) return Results.NotFound();
    return Results.Ok(new
    {
        c.Name,
        maps = c.Hashes.Select(h => new { md5 = h, beatmap = osu.FindByMd5(h) }),
    });
});

// Create / add / remove maps / rename, depending on which fields are set.
api.MapPost("/collection", (OsuInstall osu, CollectionRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Name is required." });
    var db = osu.ReadCollections();
    if (req.NewName is { Length: > 0 } newName)
    {
        var c = db.Collections.FirstOrDefault(x => x.Name == req.Name);
        if (c is null) return Results.NotFound(new { error = "Collection not found." });
        if (db.Collections.Any(x => x.Name == newName)) return Results.Conflict(new { error = "A collection with that name already exists." });
        db.Collections[db.Collections.IndexOf(c)] = c with { Name = newName };
    }
    else
    {
        var c = db.GetOrCreate(req.Name);
        foreach (var h in req.Add ?? []) if (!c.Hashes.Contains(h)) c.Hashes.Add(h);
        if (req.Remove is { } remove) c.Hashes.RemoveAll(remove.Contains);
    }
    return Results.Ok(new { backup = osu.WriteCollections(db) });
});

// Import an exported collection file. Step 1 creates the collection; step 2 (a job) finds and downloads missing maps.
api.MapPost("/collection/import", (CollectionImporter importer, ImportRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "The file has no collection name." });
    return Results.Ok(importer.Import(req.Name.Trim(), req.Maps ?? []));
});

api.MapPost("/collection/restore", (JobManager jobs, CollectionImporter importer, ImportRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Name is required." });
    if (OsuInstall.IsGameRunning)
        return Results.Conflict(new { error = "osu! is running. Close it first, or it will overwrite collection.db when it exits." });
    var job = jobs.Start("restore", async (job, ct) => await importer.RestoreAsync(req.Name.Trim(), req.Maps ?? [], job, ct));
    return Results.Ok(new { job.Id });
});

api.MapDelete("/collection", (OsuInstall osu, string name) =>
{
    var db = osu.ReadCollections();
    if (db.Collections.RemoveAll(c => c.Name == name) == 0) return Results.NotFound();
    return Results.Ok(new { backup = osu.WriteCollections(db) });
});

// ---- Online: profile, search, downloads ----------------------------------------------------

api.MapGet("/profile/{user}", async (OsuWebClient web, OsuInstall osu, string user, CancellationToken ct) =>
{
    var p = await web.GetProfileAsync(user, 100, ct);
    return new
    {
        p.Id, p.Username, p.Pp, p.GlobalRank, p.CountryRank, p.Accuracy,
        comfortStars = TrainingPlanner.ComfortStars(p),
        topPlays = p.TopPlays.Select(t => new { play = t, installed = osu.FindByBeatmapId(t.BeatmapId) is not null }),
    };
});

api.MapGet("/search", async (MirrorClient mirror, OsuInstall osu, string q, int offset = 0, bool ranked = true, CancellationToken ct = default) =>
{
    var sets = await mirror.SearchAsync(q, 50, offset, ranked, ct);
    return sets.Select(s => new { set = s, s.CoverUrl, s.PreviewUrl, installed = osu.HasSet(s.Id) });
});

api.MapPost("/download", (JobManager jobs, TrainingPlanner planner, OsuInstall osu, DownloadRequest req) =>
{
    if (req.Collection is { Length: > 0 } && OsuInstall.IsGameRunning)
        return Results.Conflict(new { error = "osu! is running. Close it before downloading into a collection." });
    var job = jobs.Start("download", async (job, ct) =>
    {
        var hashes = await planner.DownloadSetsAsync(req.SetIds, job, ct);
        if (req.Collection is { Length: > 0 } name && hashes.Count > 0)
        {
            var db = osu.ReadCollections();
            var c = db.GetOrCreate(name);
            foreach (var h in hashes.Values.SelectMany(x => x.Values)) if (!c.Hashes.Contains(h)) c.Hashes.Add(h);
            osu.WriteCollections(db);
            job.Report($"Added {hashes.Values.Sum(x => x.Count)} difficulties to \"{name}\"");
        }
        return hashes.Keys;
    });
    return Results.Ok(new { job.Id });
});

// ---- Training planner ----------------------------------------------------------------------

api.MapGet("/plan/categories", () => TrainingPlanner.Catalog.Select(c => new
{
    c.Id, c.Name, c.Description, c.MinOffset, c.MaxOffset, c.Family, c.Tags,
}));

api.MapPost("/plan", (JobManager jobs, TrainingPlanner planner, PlanOptions options) =>
{
    if (string.IsNullOrWhiteSpace(options.Username)) return Results.BadRequest(new { error = "Username is required." });
    var job = jobs.Start("plan", async (job, ct) => await planner.GenerateAsync(options, job, ct));
    return Results.Ok(new { job.Id });
});

api.MapPost("/plan/apply", (JobManager jobs, TrainingPlanner planner, TrainingPlan plan) =>
{
    if (OsuInstall.IsGameRunning)
        return Results.Conflict(new { error = "osu! is running. Close it first, or it will overwrite collection.db when it exits." });
    var job = jobs.Start("apply", async (job, ct) => await planner.ApplyAsync(plan, job, ct));
    return Results.Ok(new { job.Id });
});

// ---- Jobs ----------------------------------------------------------------------------------

api.MapGet("/jobs", (JobManager jobs) => jobs.All.Select(j => new { j.Id, j.Kind, j.State, j.Status, j.Done, j.Total, j.Started }));
api.MapGet("/jobs/{id}", (JobManager jobs, string id) => jobs.Get(id) is { } j ? Results.Ok(j) : Results.NotFound());
api.MapPost("/jobs/{id}/cancel", (JobManager jobs, string id) => { jobs.Cancel(id); return Results.Ok(); });

// Open the browser once the server is really listening (a published exe; `dotnet run` opens it via launchSettings).
if (!app.Environment.IsDevelopment() && !args.Contains("--no-browser"))
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        Console.WriteLine();
        Console.WriteLine($"  osu! Collection Manager is running at {appUrl}");
        Console.WriteLine("  Your browser should open by itself. Close this window to quit.");
        Console.WriteLine();
        OpenBrowser(appUrl);
    });
}

try
{
    app.Run();
}
catch (IOException e) when (e.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine($"Could not start: another program is already using {appUrl}. Close it, or start this app with --urls=http://localhost:<other port>.");
    if (Environment.UserInteractive && !Console.IsInputRedirected)
    {
        Console.WriteLine("Press any key to close.");
        Console.ReadKey(true);
    }
}

static string? FindWebRoot(string startFolder)
{
    for (var dir = new DirectoryInfo(startFolder); dir is not null; dir = dir.Parent)
    {
        string candidate = Path.Combine(dir.FullName, "wwwroot");
        if (File.Exists(Path.Combine(candidate, "index.html"))) return candidate;
    }
    return null;
}

static void OpenBrowser(string url)
{
    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
    catch (Exception e) { Console.WriteLine($"Could not open the browser automatically ({e.Message}). Open {url} yourself."); }
}

static Task WriteSetupRequired(HttpContext ctx, string message)
{
    ctx.Response.StatusCode = StatusCodes.Status409Conflict;
    return ctx.Response.WriteAsJsonAsync(new { error = message, setupRequired = true });
}

record SetupRequest(string? Path);
record ImportRequest(string Name, ImportMap[]? Maps);
record CollectionRequest(string Name, string? NewName, string[]? Add, string[]? Remove);
record DownloadRequest(int[] SetIds, string? Collection);
