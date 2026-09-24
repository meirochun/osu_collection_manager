namespace OsuCollectionManager.Services;

public sealed record PlanMap(
    int SetId, int BeatmapId, string Artist, string Title, string Version, string Creator,
    double Stars, double Bpm, int Length, string Checksum, bool Installed, Dictionary<string, int> Tags,
    string? Family = null, double FamilyShare = 0);

public sealed record PlanCollection(string Name, string Description, List<PlanMap> Maps);

public sealed record TrainingPlan(string Username, double BaseStars, int CandidateSets, int TaggedSets, List<PlanCollection> Collections);

public sealed record PlanOptions
{
    public string Username { get; init; } = "";
    public string Prefix { get; init; } = "Training";
    /// <summary>Mirror search terms. Empty = derive them from the selected categories.</summary>
    public string[] Queries { get; init; } = [];
    /// <summary>Category ids to generate (see <see cref="TrainingPlanner.Catalog"/>). Empty = all of them.</summary>
    public string[] Categories { get; init; } = [];
    /// <summary>Shifts every category's star range, e.g. +0.5 for a harder plan.</summary>
    public double StarOffset { get; init; } = 0;
    /// <summary>Splits each pattern category into this many collections ordered by stars (1-4).</summary>
    public int Tiers { get; init; } = 1;
    /// <summary>
    /// How much community tag support a map needs for a category (weighted votes, see <see cref="TrainingPlanner"/>).
    /// Higher = fewer but more clear-cut maps.
    /// </summary>
    public double MinTagVotes { get; init; } = 2;
    public bool IncludeRevisit { get; init; } = true;
    public bool IncludeFarm { get; init; } = true;
    /// <summary>How many beatmapset pages to read for community tags (~1.1 s each).</summary>
    public int TagLookups { get; init; } = 300;
    public int MapsPerCollection { get; init; } = 12;
    public int SightReadingMaps { get; init; } = 20;
    /// <summary>Extra beatmap ids to put in the "Revisit" collection (e.g. a map that recently went badly).</summary>
    public int[] RevisitBeatmapIds { get; init; } = [];
    public double RevisitBelowAccuracy { get; init; } = 97.5;
}

/// <summary>
/// One training category: a star range relative to the player's comfort level, plus the
/// community tags that identify the pattern type.
/// </summary>
internal sealed record Category(
    string Id, string Name, string Description, double MinOffset, double MaxOffset, string[] Tags,
    Func<CandidateMap, bool>? Filter = null, bool Random = false, bool SortByBpm = false,
    string[]? SearchTerms = null, string? Family = null);

internal sealed record CandidateMap(OnlineSet Set, OnlineBeatmap Map, Dictionary<string, int> Tags, int Favourites)
{
    private Dictionary<string, double>? _familyScores;

    /// <summary>Weighted tag votes per skill family, worked out once (selection looks at every candidate many times).</summary>
    public Dictionary<string, double> FamilyScores => _familyScores ??= TrainingPlanner.ComputeFamilyScores(Tags);
}

public sealed class TrainingPlanner(OsuInstall osu, OsuWebClient web, MirrorClient mirror)
{
    private static readonly string[] Jumps = ["skillset/jumps", "jumps/wide", "jumps/sharp", "jumps/cross-screen", "jumps/triangles", "jumps/back and forth", "jumps/linear", "jumps/squares", "jumps/stars"];
    private static readonly string[] Flow = ["streams/flow aim", "streams/spaced streams", "sliders/high sv"];
    private static readonly string[] Tech = ["skillset/tech", "tech/slider tech", "tech/aim control", "tech/finger control", "sliders/complex sv", "sliders/complex slidershapes", "meta/variable timing"];
    private static readonly string[] Stamina = ["streams/stamina", "skillset/streams"];
    private static readonly string[] Speed = ["streams/speed", "streams/bursts", "streams/doubles", "streams/quads", "streams/cutstreams"];

    // ---- Keeping categories consistent -------------------------------------------------------------------------------
    // Community tags are votes, and a map usually has votes for several skills at once (a jump map with a stream section
    // carries both "skillset/jumps" and "skillset/streams"). Each tag belongs to exactly one skill family, and a map is only
    // offered to a category if that category's family is clearly the map's main skill. Thresholds come from measuring
    // ~570 real ranked difficulties: with the old "any single vote" rule 30-70% of picks belonged to a different skill.

    /// <summary>Skill families. Stamina and Speed are two views of the same "stream" family.</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> Families = new Dictionary<string, string[]>
    {
        ["jump"] = Jumps,
        ["flow"] = Flow,
        ["tech"] = Tech,
        ["stream"] = [.. Stamina, .. Speed],
    };

    /// <summary>Order used to break exact ties between families, so the result never depends on dictionary order.</summary>
    private static readonly string[] FamilyOrder = ["jump", "flow", "tech", "stream"];

    /// <summary>Generic "skillset/..." tags are attached to far more maps than specific ones, so they count for less.</summary>
    private static readonly HashSet<string> BroadTags = ["skillset/jumps", "skillset/streams", "skillset/tech"];
    private const double BroadTagWeight = 0.5;

    /// <summary>The category's family must hold at least this share of the map's votes (and be its top family).</summary>
    private const double DominantShare = 0.5;

    /// <summary>Objects (circles + sliders) per second. Jump maps measured up to ~5.2 at the 90th percentile, stream maps start ~4.</summary>
    private const double JumpMaxObjectsPerSecond = 5.5;
    private const double StreamMinObjectsPerSecond = 4.0;

    private static double TagVotes(Dictionary<string, int> tags, IEnumerable<string> names) =>
        names.Sum(name => tags.GetValueOrDefault(name) * (BroadTags.Contains(name) ? BroadTagWeight : 1.0));

    internal static Dictionary<string, double> ComputeFamilyScores(Dictionary<string, int> tags) =>
        Families.ToDictionary(family => family.Key, family => TagVotes(tags, family.Value));

    /// <summary>Share (0-1) of the map's family votes that belong to <paramref name="family"/>.</summary>
    internal static double FamilyShare(CandidateMap c, string family)
    {
        double total = c.FamilyScores.Values.Sum();
        return total > 0 ? c.FamilyScores[family] / total : 0;
    }

    private static bool IsDominantFamily(CandidateMap c, string family, double minVotes)
    {
        double own = c.FamilyScores[family];
        string top = c.FamilyScores
            .OrderByDescending(score => score.Value)
            .ThenBy(score => Array.IndexOf(FamilyOrder, score.Key))
            .First().Key;
        return own >= minVotes && top == family && FamilyShare(c, family) >= DominantShare;
    }

    /// <summary>Cheap sanity check on the map's real density, for maps whose tags alone are thin.</summary>
    private static bool DensityFits(Category cat, CandidateMap c)
    {
        double perSecond = (c.Map.Circles + c.Map.Sliders) / (double)Math.Max(c.Map.Length, 1);
        return cat.Family switch
        {
            "jump" => perSecond <= JumpMaxObjectsPerSecond,
            "stream" => perSecond >= StreamMinObjectsPerSecond,
            _ => true,
        };
    }

    /// <summary>Search terms used when the user leaves the query box empty and a category has none of its own.</summary>
    private static readonly string[] DefaultQueries =
        ["Sotarks", "ExPew", "Fort", "Monstrata", "Camellia", "Demetori", "Extra Stage", "touhou",
         "stream", "tech", "jump", "metal", "vocaloid", "anime", "Akitoshi", "Lasse", "DeviousPanda", "Mazzerin", "Kroytz"];

    /// <summary>Every training category the planner knows. The ids are what the UI sends in <c>PlanOptions.Categories</c>.</summary>
    internal static readonly IReadOnlyList<Category> Catalog =
    [
        new("flow", "Flow Aim", "Continuous curved aim: spaced streams and flowing sliders.", -0.1, 1.0, Flow,
            SearchTerms: ["flow aim", "flow", "spaced stream"], Family: "flow"),
        new("jumps", "Jump Aim (+1 step)", "Wide, irregular jumps one step above the comfort zone.", 0.6, 1.4, Jumps, c => c.Map.Bpm >= 170,
            SearchTerms: ["jump", "wide jump", "aim"], Family: "jump"),
        new("sight", "Sight-reading", "Random unseen maps at your level. Play once, no restarts.", -0.4, 0.4, [], Random: true),
        new("tech", "Tech", "Broken rhythms, slider tech and finger control.", -0.4, 0.6, Tech,
            SearchTerms: ["tech", "slider tech", "finger control", "rhythm"], Family: "tech"),
        new("stamina", "Stream Endurance", "Long continuous streams (2 min+).", -0.6, 0.6, Stamina, c => c.Map.Length >= 120, SortByBpm: true,
            SearchTerms: ["stream", "stamina", "marathon"], Family: "stream"),
        new("speed", "Speed (increasing BPM)", "Bursts and speed streams; play in ascending BPM order.", -0.6, 0.8, Speed, SortByBpm: true,
            SearchTerms: ["speed", "burst", "stream"], Family: "stream"),
        new("nofail", "No Fail (+0.5 stars)", "Play with No Fail to get used to higher density and BPM.", 0.9, 1.5, [], c => c.Map.Bpm is >= 175 and <= 235),
    ];

    /// <summary>How strongly the community tagged this map for the category (used to rank maps inside a category).</summary>
    private static double Score(Category cat, CandidateMap c) => TagVotes(c.Tags, cat.Tags);

    /// <summary>
    /// Does this candidate belong in the category's pool (stars, filter, main skill and density),
    /// ignoring what other collections already took? Categories without a family (sight-reading, no fail) take any map.
    /// </summary>
    private static bool Matches(Category cat, CandidateMap c, double min, double max, double minTagVotes) =>
        c.Map.Stars >= min && c.Map.Stars < max
        && (cat.Filter?.Invoke(c) ?? true)
        && (cat.Family is null || (IsDominantFamily(c, cat.Family, minTagVotes) && DensityFits(cat, c)));

    private static IEnumerable<string> SearchQueries(PlanOptions o, IReadOnlyList<Category> selected)
    {
        if (o.Queries.Length > 0) return o.Queries;

        // Categories without their own terms (sight-reading, no fail) can use any map, so fall back to the broad list.
        var terms = selected.SelectMany(c => c.SearchTerms ?? []).Distinct().ToList();
        if (selected.Any(c => c.SearchTerms is null)) terms.AddRange(DefaultQueries);
        return terms.Distinct();
    }

    /// <summary>Median nomod star rating of the player's top plays without speed mods.</summary>
    public static double ComfortStars(UserProfile p)
    {
        var stars = p.TopPlays.Where(t => !t.Mods.Contains("DT") && !t.Mods.Contains("NC") && !t.Mods.Contains("HT"))
            .Select(t => t.Stars).Order().ToList();
        if (stars.Count == 0) stars = p.TopPlays.Select(t => t.Stars).Order().ToList();
        return stars.Count == 0 ? 5.0 : Math.Round(stars[stars.Count / 2], 2);
    }

    public async Task<TrainingPlan> GenerateAsync(PlanOptions o, Job job, CancellationToken ct)
    {
        job.Status = "Reading profile";
        var profile = await web.GetProfileAsync(o.Username, 100, ct);
        double b = ComfortStars(profile);
        job.Report($"{profile.Username}: {profile.Pp:0}pp, #{profile.GlobalRank}. Comfort level ≈ {b:0.00}★ (median of nomod top plays)");

        var selected = o.Categories.Length == 0
            ? Catalog
            : Catalog.Where(c => o.Categories.Contains(c.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0)
            throw new InvalidOperationException("None of the selected training categories exist.");
        int tiers = Math.Clamp(o.Tiers, 1, 4);
        job.Report("Focus: " + string.Join(", ", selected.Select(c => c.Name)) +
                   (o.StarOffset != 0 ? $" ({o.StarOffset:+0.0;-0.0}★ shift)" : ""));

        // 1. Candidate sets from mirror text searches.
        var sets = new Dictionary<int, OnlineSet>();
        var foundVia = new Dictionary<int, HashSet<string>>(); // set id -> the search terms that returned it
        var queries = SearchQueries(o, selected).ToList();
        job.Total = queries.Count;
        foreach (var q in queries)
        {
            job.Status = $"Searching \"{q}\"";
            for (int offset = 0; offset < 500; offset += 100)
            {
                List<OnlineSet> page;
                try { page = await mirror.SearchAsync(q, 100, offset, rankedOnly: true, ct); }
                catch (HttpRequestException e) { job.Report($"search \"{q}\" failed: {e.Message}"); break; }
                foreach (var s in page)
                {
                    sets.TryAdd(s.Id, s);
                    if (!foundVia.TryGetValue(s.Id, out var terms)) foundVia[s.Id] = terms = [];
                    terms.Add(q);
                }
                if (page.Count < 100) break;
            }
            job.Done++;
            job.Report($"\"{q}\": {sets.Count} candidate sets so far");
        }

        // 2. Community tags. Reading them is the slow part (~1.1 s per set), so spend the lookups where they are needed:
        //    every category gets its own queue of sets that are new to the player, have a difficulty in that category's star
        //    range and (with automatic queries) were found by that category's search terms. Categories then take turns, and a
        //    category leaves the rotation once it has enough candidates, so a narrow category (jumps) is not starved by
        //    broad ones (streams) and the run stops as soon as everything is covered.
        double lo = b + selected.Min(c => c.MinOffset) + o.StarOffset;
        double hi = b + selected.Max(c => c.MaxOffset) + o.StarOffset;
        job.Report($"Looking for maps between {lo:0.0}★ and {hi:0.0}★");

        var newSets = sets.Values.Where(s => !osu.HasSet(s.Id)).ToList();
        bool relevantByTerms = o.Queries.Length == 0; // custom queries don't say anything about which category a set fits
        bool Relevant(Category cat, OnlineSet s)
        {
            double min = b + cat.MinOffset + o.StarOffset, max = b + cat.MaxOffset + o.StarOffset;
            return s.Beatmaps.Any(m => m.Stars >= min && m.Stars < max && m.Length is >= 60 and <= 330)
                   && (!relevantByTerms || cat.SearchTerms is null || foundVia[s.Id].Overlaps(cat.SearchTerms));
        }

        var candidates = new List<CandidateMap>();
        var rotation = new Queue<(Category Category, int Needed, Queue<OnlineSet> Sets)>(selected.Select(cat => (
            cat,
            Needed: 3 * (cat.Random ? o.SightReadingMaps : o.MapsPerCollection * tiers), // 3x: room to choose the best
            Sets: new Queue<OnlineSet>(newSets.Where(s => Relevant(cat, s)).OrderBy(_ => System.Random.Shared.Next())))));
        var lookedUp = new HashSet<int>();

        OnlineSet? NextSet()
        {
            for (int turns = rotation.Count; turns > 0; turns--)
            {
                var entry = rotation.Dequeue();
                if (MatchingSetCount(entry.Category, candidates, b, o.StarOffset, o.MinTagVotes) >= entry.Needed) continue; // covered
                while (entry.Sets.TryDequeue(out var next))
                {
                    if (!lookedUp.Add(next.Id)) continue; // already read for another category
                    rotation.Enqueue(entry);
                    return next;
                }
                // Nothing left to read for this category: drop it from the rotation.
            }
            return null;
        }

        job.Done = 0;
        job.Total = Math.Min(o.TagLookups, newSets.Count);
        while (job.Done < o.TagLookups && NextSet() is { } s)
        {
            ct.ThrowIfCancellationRequested();
            job.Status = $"Reading tags {job.Done + 1}/{job.Total}: {s.Artist} - {s.Title}";
            SetTags? tags = null;
            try { tags = await web.GetSetTagsAsync(s.Id, ct); }
            catch (HttpRequestException e) { job.Report($"tags for {s.Id} failed: {e.Message}"); }
            job.Done++;
            if (tags is null || tags.Status is not ("ranked" or "approved")) continue;
            foreach (var m in s.Beatmaps.Where(m => m.Length is >= 60 and <= 330))
                candidates.Add(new CandidateMap(s, m, tags.ByBeatmap.GetValueOrDefault(m.BeatmapId) ?? [], tags.FavouriteCount));
        }
        if (job.Done < o.TagLookups)
        {
            job.Report($"Stopped after {job.Done} lookups: every category has enough candidates or no more relevant sets are left");
            job.Total = job.Done;
        }
        job.Report($"Tagged {job.Done} sets, {candidates.Count} candidate difficulties");

        // 3. Pick maps per category, never reusing a set across collections.
        job.Status = "Selecting maps";
        var used = new HashSet<int>();
        var collections = new List<PlanCollection>();
        int n = 1;
        foreach (var cat in selected)
        {
            double min = b + cat.MinOffset + o.StarOffset, max = b + cat.MaxOffset + o.StarOffset;
            int categoryTiers = cat.Random ? 1 : tiers; // random sight-reading has no difficulty order to split
            int count = (cat.Random ? o.SightReadingMaps : o.MapsPerCollection) * categoryTiers;

            var pool = candidates.Where(c => !used.Contains(c.Set.Id) && Matches(cat, c, min, max, o.MinTagVotes));
            pool = cat.Random
                ? pool.OrderBy(_ => System.Random.Shared.Next())
                : pool.OrderByDescending(c => Score(cat, c)).ThenByDescending(c => c.Favourites);

            var picked = pool.DistinctBy(c => c.Set.Id).Take(count).ToList();
            used.UnionWith(picked.Select(c => c.Set.Id));

            // Being strict about the skill means a narrow category can come up short; say so instead of padding it.
            string shortfall = picked.Count > 0 && picked.Count < count
                ? $" Only {picked.Count} of {count} maps clearly fit: try more tag lookups, a lower minimum tag votes, or a different difficulty."
                : "";

            if (categoryTiers == 1 || picked.Count == 0)
            {
                if (cat.SortByBpm) picked = picked.OrderBy(c => c.Map.Bpm).ToList();
                collections.Add(new PlanCollection($"{o.Prefix} {n++} - {cat.Name}",
                    $"{cat.Description} {min:0.0}–{max:0.0}★{shortfall}", ToPlanMaps(picked, cat)));
                job.Report($"{cat.Name}: {picked.Count} of {count} maps");
                continue;
            }

            // Difficulty ladder: the easiest chunk becomes tier 1, the hardest tier N.
            var byStars = picked.OrderBy(c => c.Map.Stars).ToList();
            int chunkSize = (int)Math.Ceiling(byStars.Count / (double)categoryTiers);
            for (int tier = 0; tier < categoryTiers; tier++)
            {
                var chunk = byStars.Skip(tier * chunkSize).Take(chunkSize).ToList();
                if (chunk.Count == 0) break;
                if (cat.SortByBpm) chunk = chunk.OrderBy(c => c.Map.Bpm).ToList();
                collections.Add(new PlanCollection($"{o.Prefix} {n++} - {cat.Name} (Tier {tier + 1}/{categoryTiers})",
                    $"{cat.Description} {chunk.Min(c => c.Map.Stars):0.0}–{chunk.Max(c => c.Map.Stars):0.0}★", ToPlanMaps(chunk, cat)));
                job.Report($"{cat.Name} tier {tier + 1}: {chunk.Count} maps");
            }
        }

        // 4. Collections built from the player's own top plays (already installed).
        if (o.IncludeRevisit)
        {
            var revisit = profile.TopPlays.Where(t => t.Accuracy < o.RevisitBelowAccuracy).Select(t => t.BeatmapId)
                .Concat(o.RevisitBeatmapIds).Distinct();
            collections.Add(new PlanCollection($"{o.Prefix} {n++} - Revisit (low accuracy)",
                $"Top plays under {o.RevisitBelowAccuracy}% plus maps you flagged. Replay until stable above 90%.",
                LocalMaps(revisit)));
        }
        if (o.IncludeFarm)
        {
            collections.Add(new PlanCollection($"{o.Prefix} {n} - Farm (known top plays)",
                "The 70% half of the 70/30 farming split: maps you already play well.",
                LocalMaps(profile.TopPlays.Select(t => t.BeatmapId))));
        }

        return new TrainingPlan(profile.Username, b, sets.Count, job.Done, collections);
    }

    /// <summary>How many distinct sets currently have a map that fits the category.</summary>
    private static int MatchingSetCount(Category cat, List<CandidateMap> candidates, double comfort, double starOffset, double minTagVotes)
    {
        double min = comfort + cat.MinOffset + starOffset, max = comfort + cat.MaxOffset + starOffset;
        return candidates.Where(c => Matches(cat, c, min, max, minTagVotes)).Select(c => c.Set.Id).Distinct().Count();
    }

    /// <summary>Turns picked candidates into plan rows, remembering which skill family they were chosen for and how clear-cut it is.</summary>
    private static List<PlanMap> ToPlanMaps(IEnumerable<CandidateMap> picked, Category cat) =>
        picked.Select(c => ToPlanMap(c.Set, c.Map, c.Tags, installed: false) with
        {
            Family = cat.Family,
            FamilyShare = cat.Family is null ? 0 : Math.Round(FamilyShare(c, cat.Family), 2),
        }).ToList();

    private List<PlanMap> LocalMaps(IEnumerable<int> beatmapIds) =>
        beatmapIds.Select(osu.FindByBeatmapId).OfType<Osu.LocalBeatmap>()
            .Select(m => new PlanMap(m.SetId, m.BeatmapId, m.Artist, m.Title, m.Version, m.Creator, m.StarRating,
                m.MaxBpm, m.DrainSeconds, m.Md5, true, []))
            .ToList();

    private static PlanMap ToPlanMap(OnlineSet s, OnlineBeatmap m, Dictionary<string, int> tags, bool installed) =>
        new(s.Id, m.BeatmapId, s.Artist, s.Title, m.Version, s.Creator, m.Stars, m.Bpm, m.Length, m.Checksum, installed, tags);

    /// <summary>Downloads everything the plan needs, then writes the collections.</summary>
    public async Task<object> ApplyAsync(TrainingPlan plan, Job job, CancellationToken ct)
    {
        var hashes = await DownloadSetsAsync(plan.Collections.SelectMany(c => c.Maps).Where(m => !m.Installed).Select(m => m.SetId), job, ct);

        job.Status = "Writing collection.db";
        var db = osu.ReadCollections();
        var summary = new List<object>();
        foreach (var pc in plan.Collections.Where(c => c.Maps.Count > 0))
        {
            var list = new List<string>();
            foreach (var m in pc.Maps)
            {
                if (osu.FindByBeatmapId(m.BeatmapId) is { } local) list.Add(local.Md5);
                else if (hashes.TryGetValue(m.SetId, out var h)) list.Add(h.GetValueOrDefault(m.BeatmapId) ?? m.Checksum);
                else if (osu.HasSet(m.SetId) && m.Checksum.Length > 0) list.Add(m.Checksum); // .osz already waiting in Songs
                // else: download failed, skip the map
            }
            db.Collections.RemoveAll(c => c.Name == pc.Name);
            db.Collections.Add(new Osu.Collection(pc.Name, list.Distinct().ToList()));
            summary.Add(new { pc.Name, Maps = list.Count });
            job.Report($"Collection \"{pc.Name}\": {list.Count} maps");
        }
        var backup = osu.WriteCollections(db);
        job.Report($"collection.db written (backup: {Path.GetFileName(backup)})");
        return new { collections = summary, backup };
    }

    /// <summary>Downloads sets 3 at a time and returns md5 per beatmap id for each successful set.</summary>
    public async Task<Dictionary<int, Dictionary<int, string>>> DownloadSetsAsync(IEnumerable<int> setIds, Job job, CancellationToken ct)
    {
        var todo = setIds.Distinct().Where(id => !osu.HasSet(id)).ToList();
        var hashes = new Dictionary<int, Dictionary<int, string>>();
        job.Done = 0;
        job.Total = todo.Count;
        job.Status = $"Downloading {todo.Count} sets";
        long bytes = 0;
        string songs = osu.SongsPath;

        await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct }, async (id, token) =>
        {
            var r = await mirror.DownloadAsync(id, songs, token);
            lock (hashes)
            {
                job.Done++;
                if (r.Success)
                {
                    hashes[id] = r.HashesByBeatmapId;
                    bytes += r.Bytes;
                    job.Report($"OK   {Path.GetFileName(r.FilePath)} ({r.Bytes / 1e6:0.0} MB via {r.Mirror})");
                }
                else job.Report($"FAIL {id}: {r.Error}");
                job.Status = $"Downloading {job.Done}/{todo.Count} ({bytes / 1e6:0} MB)";
            }
        });
        job.Report($"Downloaded {hashes.Count}/{todo.Count} sets, {bytes / 1e6:0} MB. osu! imports them on next launch.");
        return hashes;
    }
}
