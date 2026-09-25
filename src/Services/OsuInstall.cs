using System.Diagnostics;
using OsuCollectionManager.Osu;

namespace OsuCollectionManager.Services;

/// <summary>Thrown when something needs the osu! folder before the user has picked one.</summary>
public sealed class OsuNotConfiguredException() : InvalidOperationException("The osu! folder has not been set up yet.");

public sealed class OsuInstall
{
    private readonly IConfiguration _config;
    private readonly UserSettingsStore _settings;
    private readonly ILogger<OsuInstall> _logger;
    private readonly Lock _lock = new();
    private volatile string? _root;
    private OsuDatabase? _db;
    private DateTime _dbStamp;
    private Dictionary<string, LocalBeatmap> _byMd5 = [];
    private Dictionary<int, LocalBeatmap> _byBeatmapId = [];
    private HashSet<int> _setIds = [];

    public OsuInstall(IConfiguration config, UserSettingsStore settings, ILogger<OsuInstall> logger)
    {
        _config = config;
        _settings = settings;
        _logger = logger;
        _ignoreRunningGame = config.GetValue("IgnoreRunningGame", false);
        _root = ResolveRoot();
    }

    /// <summary>The osu! install folder, or null until the user has chosen one.</summary>
    public string? Root => _root;
    public bool IsConfigured => _root is not null;

    /// <summary>True when a folder was chosen but is gone now (drive unplugged, osu! uninstalled).</summary>
    public bool IsFolderMissing => _root is not null && !OsuLocator.IsValid(_root);

    private string RequireRoot() => _root ?? throw new OsuNotConfiguredException();
    // On Linux file names are case-sensitive: find the real "osu!.db" / "collection.db" whatever case they were created in.
    public string OsuDbPath => PathCase.FindFile(RequireRoot(), "osu!.db") ?? Path.Combine(RequireRoot(), "osu!.db");
    public string CollectionDbPath => PathCase.FindFile(RequireRoot(), "collection.db") ?? Path.Combine(RequireRoot(), "collection.db");

    private bool AutoDetectEnabled => _config.GetValue("AutoDetect", true);

    /// <summary>Installs found on this PC, for the setup screen. Empty when auto-detection is switched off.</summary>
    public IReadOnlyList<OsuCandidate> FindCandidates() => AutoDetectEnabled ? OsuLocator.FindCandidates() : [];

    /// <summary>Command line / environment override, then the saved choice, then auto-detection.</summary>
    private string? ResolveRoot()
    {
        string? configured = _config["OsuPath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (OsuLocator.Normalize(configured) is { } fromConfig) return fromConfig;
            _logger.LogWarning("OsuPath \"{Path}\" does not contain osu!.db; ignoring it", configured);
        }

        if (OsuLocator.Normalize(_settings.Load().OsuPath) is { } saved) return saved;

        if (AutoDetectEnabled && OsuLocator.AutoPick(OsuLocator.FindCandidates()) is { } detected)
        {
            _logger.LogInformation("Found osu! at {Path}", detected);
            return detected;
        }
        return null;
    }

    /// <summary>Uses (and remembers) the given osu! folder. Throws ArgumentException with a friendly message if it isn't one.</summary>
    public string SetRoot(string? input)
    {
        string root = OsuLocator.Normalize(input)
            ?? throw new ArgumentException("osu!.db was not found there. Choose the folder that contains osu!.db and your Songs folder.");

        lock (_lock)
        {
            _root = root;
            _db = null; // force a re-read from the new folder
            _dbStamp = default;
            _byMd5 = [];
            _byBeatmapId = [];
            _setIds = [];
        }

        var settings = _settings.Load();
        settings.OsuPath = root;
        _settings.Save(settings);
        _logger.LogInformation("osu! folder set to {Path}", root);
        return root;
    }

    public string SongsPath
    {
        get
        {
            // BeatmapDirectory lives in osu!.<user>.cfg and may be relative to the osu! folder.
            string root = RequireRoot();
            var configFiles = Directory.EnumerateFiles(root).Where(f =>
                Path.GetFileName(f).StartsWith("osu!.", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(f).EndsWith(".cfg", StringComparison.OrdinalIgnoreCase));
            foreach (var cfg in configFiles)
            {
                var line = File.ReadLines(cfg).FirstOrDefault(l => l.StartsWith("BeatmapDirectory", StringComparison.OrdinalIgnoreCase));
                if (line?.Split('=', 2) is [_, var value] && value.Trim().Length > 0)
                    return ResolveSongsFolder(root, value.Trim());
            }
            return PathCase.FindDirectory(root, "Songs") ?? Path.Combine(root, "Songs");
        }
    }

    /// <summary>
    /// Turns the BeatmapDirectory setting into a real folder. On Windows that's the value itself (or relative to the osu! folder).
    /// Under Wine on Linux the value is usually a Windows path such as "D:\Games\osu!\Songs", which is translated through the
    /// Wine prefix's drive letters; if that fails the Songs folder inside the osu! folder is used.
    /// </summary>
    private static string ResolveSongsFolder(string root, string setting)
    {
        if (OperatingSystem.IsWindows())
            return Path.IsPathRooted(setting) ? setting : Path.Combine(root, setting);

        if (WinePrefix.HasDriveLetter(setting))
        {
            var translated = WinePrefix.ToLinuxPath(setting, WinePrefix.FindPrefixRoot(root));
            if (translated is not null && Directory.Exists(translated)) return translated;
        }
        else if (setting.StartsWith('/'))
        {
            return setting;
        }
        else
        {
            // Relative to the osu! folder, possibly written with backslashes ("Songs\Extra").
            var relative = PathCase.ResolveRelative(root, setting);
            if (Directory.Exists(relative)) return relative;
        }
        return PathCase.FindDirectory(root, "Songs") ?? Path.Combine(root, "Songs");
    }

    /// <summary>Test switch (--IgnoreRunningGame=true) for working on a scratch copy of the osu! folder while the game is open.</summary>
    private static bool _ignoreRunningGame;

    public static bool IsGameRunning => !_ignoreRunningGame && IsOsuProcessRunning();

    private static bool IsOsuProcessRunning()
    {
        // Under Wine the process is "osu!.exe" and only shows up in /proc; on Windows it's a normal "osu!" process.
        if (OperatingSystem.IsLinux()) return OsuLocator.LinuxOsuProcessIds().Any();
        return Process.GetProcessesByName("osu!").Length > 0;
    }

    public OsuDatabase Database
    {
        get
        {
            lock (_lock)
            {
                var stamp = File.GetLastWriteTimeUtc(OsuDbPath);
                if (_db is null || stamp != _dbStamp)
                {
                    var sw = Stopwatch.StartNew();
                    _db = OsuDatabase.Read(OsuDbPath);
                    _dbStamp = stamp;
                    _byMd5 = _db.Beatmaps.Where(b => b.Md5.Length > 0).DistinctBy(b => b.Md5).ToDictionary(b => b.Md5);
                    _byBeatmapId = _db.Beatmaps.Where(b => b.BeatmapId > 0).DistinctBy(b => b.BeatmapId).ToDictionary(b => b.BeatmapId);
                    _setIds = _db.Beatmaps.Select(b => b.SetId).Where(id => id > 0).ToHashSet();
                    _logger.LogInformation("Loaded osu!.db v{Version}: {Count} beatmaps in {Ms} ms", _db.Version, _db.Beatmaps.Count, sw.ElapsedMilliseconds);
                }
                return _db;
            }
        }
    }

    public LocalBeatmap? FindByMd5(string md5) { _ = Database; return _byMd5.GetValueOrDefault(md5); }
    public LocalBeatmap? FindByBeatmapId(int id) { _ = Database; return _byBeatmapId.GetValueOrDefault(id); }

    /// <summary>Installed sets, plus .osz files waiting in Songs to be imported on next launch.</summary>
    public bool HasSet(int setId)
    {
        _ = Database;
        return _setIds.Contains(setId) || PendingImports().Contains(setId);
    }

    public HashSet<int> PendingImports()
    {
        var ids = new HashSet<int>();
        if (!Directory.Exists(SongsPath)) return ids;
        foreach (var f in Directory.EnumerateFiles(SongsPath, "*.osz"))
            if (int.TryParse(Path.GetFileName(f).Split(' ')[0], out int id)) ids.Add(id);
        return ids;
    }

    public CollectionDatabase ReadCollections() => CollectionDatabase.Read(CollectionDbPath);

    /// <summary>osu! rewrites collection.db on exit, so writing while it runs would be lost.</summary>
    public string? WriteCollections(CollectionDatabase db)
    {
        if (IsGameRunning)
            throw new InvalidOperationException("osu! is running. Close it first, or it will overwrite collection.db when it exits.");
        return db.Write(CollectionDbPath);
    }
}
