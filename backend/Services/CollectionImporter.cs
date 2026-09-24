using System.Text.RegularExpressions;
using OsuCollectionManager.Osu;

namespace OsuCollectionManager.Services;

/// <summary>One map from an exported collection file. Only the md5 is required; the ids make downloading possible.</summary>
public sealed record ImportMap(string Md5, int? BeatmapId, int? SetId);

public sealed record ImportResult(string Name, int Total, int Installed, List<ImportMap> Missing);

/// <summary>
/// Restores an exported collection. Step 1 (<see cref="Import"/>) creates the collection right away.
/// Step 2 (<see cref="RestoreAsync"/>) finds and downloads the maps the player doesn't have yet.
/// </summary>
public sealed partial class CollectionImporter(OsuInstall osu, MirrorClient mirror, TrainingPlanner planner)
{
    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex Md5Pattern();

    /// <summary>Cleans the incoming list: lower-case valid md5s, no duplicates.</summary>
    private static List<ImportMap> Clean(IEnumerable<ImportMap> maps) =>
        maps.Select(m => m with { Md5 = (m.Md5 ?? "").Trim().ToLowerInvariant() })
            .Where(m => Md5Pattern().IsMatch(m.Md5))
            .DistinctBy(m => m.Md5)
            .ToList();

    private static void AddOnce(Collection collection, string md5)
    {
        if (!collection.Hashes.Contains(md5)) collection.Hashes.Add(md5);
    }

    /// <summary>
    /// Creates (or merges into) the collection. A map that is installed but with a different md5 — the mapper
    /// updated it since the export — is matched by beatmap id and the local md5 is used, so it resolves in osu!.
    /// </summary>
    public ImportResult Import(string name, IEnumerable<ImportMap> maps)
    {
        var cleaned = Clean(maps);
        var db = osu.ReadCollections();
        var collection = db.GetOrCreate(name);

        int installed = 0;
        var missing = new List<ImportMap>();
        foreach (var map in cleaned)
        {
            var local = osu.FindByMd5(map.Md5)
                        ?? (map.BeatmapId is > 0 ? osu.FindByBeatmapId(map.BeatmapId.Value) : null);
            if (local is not null)
            {
                AddOnce(collection, local.Md5);
                installed++;
            }
            else
            {
                AddOnce(collection, map.Md5);
                missing.Add(map);
            }
        }

        osu.WriteCollections(db);
        return new ImportResult(name, cleaned.Count, installed, missing);
    }

    /// <summary>
    /// Works out which set each missing map belongs to (the file's set id, or a lookup by md5), downloads the sets
    /// that aren't in the library, and swaps in the real md5 of any map whose version changed since the export.
    /// </summary>
    public async Task<object?> RestoreAsync(string name, IEnumerable<ImportMap> maps, Job job, CancellationToken ct)
    {
        var missing = Clean(maps);

        // 1. Which set does each map belong to?
        var setOf = new Dictionary<string, int>();      // md5 -> set id
        var beatmapOf = new Dictionary<string, int>();  // md5 -> beatmap id
        int unidentified = 0;
        job.Total = missing.Count;
        job.Done = 0;
        foreach (var map in missing)
        {
            ct.ThrowIfCancellationRequested();
            job.Status = $"Identifying maps {job.Done + 1}/{missing.Count}";

            if (map.SetId is > 0)
            {
                setOf[map.Md5] = map.SetId.Value;
                if (map.BeatmapId is > 0) beatmapOf[map.Md5] = map.BeatmapId.Value;
            }
            else
            {
                (int BeatmapId, int SetId)? found = null;
                try { found = await mirror.LookupByMd5Async(map.Md5, ct); }
                catch (HttpRequestException e) { job.Report($"Lookup failed for {map.Md5}: {e.Message}"); }

                if (found is { } f)
                {
                    setOf[map.Md5] = f.SetId;
                    beatmapOf[map.Md5] = f.BeatmapId;
                }
                else
                {
                    unidentified++;
                    job.Report($"Could not find which beatmap {map.Md5} is (deleted, or an old version)");
                }
            }
            job.Done++;
        }

        // 2. Download the sets that aren't installed (or waiting as .osz in Songs).
        var wanted = setOf.Values.Distinct().ToList();
        var toDownload = wanted.Where(id => !osu.HasSet(id)).ToList();
        job.Report($"{wanted.Count} sets needed, {wanted.Count - toDownload.Count} already in your library, {toDownload.Count} to download");
        var hashes = toDownload.Count > 0
            ? await planner.DownloadSetsAsync(toDownload, job, ct)
            : new Dictionary<int, Dictionary<int, string>>();

        // 3. Replace stale md5s with the ones from the files that were actually downloaded.
        job.Status = "Updating collection.db";
        var db = osu.ReadCollections();
        var collection = db.GetOrCreate(name);
        int updated = 0;
        foreach (var map in missing)
        {
            if (!setOf.TryGetValue(map.Md5, out int setId) || !beatmapOf.TryGetValue(map.Md5, out int beatmapId)) continue;
            if (!hashes.TryGetValue(setId, out var perBeatmap) || !perBeatmap.TryGetValue(beatmapId, out var fresh) || fresh == map.Md5) continue;

            int index = collection.Hashes.IndexOf(map.Md5);
            if (index < 0) continue;
            if (collection.Hashes.Contains(fresh)) collection.Hashes.RemoveAt(index);
            else collection.Hashes[index] = fresh;
            updated++;
        }
        var backup = osu.WriteCollections(db);

        int failed = toDownload.Count - hashes.Count;
        job.Report($"Done: {hashes.Count} sets downloaded, {failed} failed, {unidentified} maps not identified, {updated} md5s updated. Start osu! to import the new maps.");
        return new { downloaded = hashes.Count, failed, unidentified, updated, alreadyInstalledSets = wanted.Count - toDownload.Count, backup };
    }
}
