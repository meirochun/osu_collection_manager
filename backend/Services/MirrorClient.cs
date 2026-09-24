using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OsuCollectionManager.Services;

/// <summary>
/// Beatmap mirror access in the style of kionell/osu-downloader: an ordered list of mirrors,
/// retry + fallback to the next one, and validation that the payload really is an .osz.
/// </summary>
public sealed partial class MirrorClient(IHttpClientFactory factory, IConfiguration config, ILogger<MirrorClient> logger)
{
    private static readonly string[] DefaultMirrors =
    [
        "https://osu.direct/api/d/{id}?noVideo=1",
        "https://catboy.best/d/{id}n",
        "https://dl.sayobot.cn/beatmaps/download/novideo/{id}",
        "https://beatconnect.io/b/{id}/",
        "https://api.nerinyan.moe/d/{id}?noVideo=true",
    ];

    private HttpClient Http => factory.CreateClient("mirror");
    private string[] Mirrors => config.GetSection("Mirrors").Get<string[]>() is { Length: > 0 } m ? m : DefaultMirrors;

    public async Task<List<OnlineSet>> SearchAsync(string query, int amount, int offset, bool rankedOnly, CancellationToken ct)
    {
        var url = $"https://osu.direct/api/v2/search?q={Uri.EscapeDataString(query)}&mode=0&amount={amount}&offset={offset}"
                  + (rankedOnly ? "&status=1" : "");
        var arr = JsonNode.Parse(await Http.GetStringAsync(url, ct))?.AsArray() ?? [];
        var sets = new List<OnlineSet>();
        foreach (var s in arr)
        {
            var status = (string?)s!["status"] ?? "";
            if (rankedOnly && status is not ("ranked" or "approved")) continue;
            var maps = s["beatmaps"]!.AsArray()
                .Where(b => (int?)b!["mode_int"] == 0)
                .Select(b => new OnlineBeatmap(
                    (int)b!["id"]!, (int)s["id"]!, (string)b["version"]!, (double)b["difficulty_rating"]!,
                    (double?)b["bpm"] ?? 0, (int?)b["hit_length"] ?? 0, (int?)b["count_circles"] ?? 0,
                    (int?)b["count_sliders"] ?? 0, (string?)b["checksum"] ?? "", (double?)b["cs"] ?? 0, (double?)b["ar"] ?? 0))
                .OrderBy(b => b.Stars).ToList();
            if (maps.Count == 0) continue;
            sets.Add(new OnlineSet((int)s["id"]!, (string)s["artist"]!, (string)s["title"]!, (string)s["creator"]!, status,
                (int?)s["favourite_count"] ?? 0, (int?)s["play_count"] ?? 0, maps));
        }
        return sets;
    }

    /// <summary>
    /// Finds which beatmap and set a .osu file hash (the md5 stored in collection.db) belongs to.
    /// Returns null when the mirror doesn't know that hash (deleted map, or an old/unsubmitted version).
    /// </summary>
    public async Task<(int BeatmapId, int SetId)?> LookupByMd5Async(string md5, CancellationToken ct)
    {
        using var res = await Http.GetAsync($"https://osu.direct/api/v2/md5/{Uri.EscapeDataString(md5)}", ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();

        var json = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        int beatmapId = (int?)json?["id"] ?? 0;
        int setId = (int?)json?["beatmapset_id"] ?? 0;
        return beatmapId > 0 && setId > 0 ? (beatmapId, setId) : null;
    }

    public async Task<DownloadResult> DownloadAsync(int setId, string songsPath, CancellationToken ct)
    {
        string? lastError = null;
        foreach (var template in Mirrors)
        {
            var url = template.Replace("{id}", setId.ToString());
            string host = new Uri(url).Host;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    using var res = await Http.GetAsync(url, ct);
                    res.EnsureSuccessStatusCode();
                    byte[] data = await res.Content.ReadAsByteArrayAsync(ct);
                    var hashes = ValidateOsz(data);

                    string name = res.Content.Headers.ContentDisposition?.FileNameStar
                                  ?? res.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                                  ?? $"{setId}.osz";
                    if (!name.StartsWith(setId + " ")) name = $"{setId} {name}";
                    name = InvalidFileChars().Replace(name, "");
                    string path = Path.Combine(songsPath, name);
                    await File.WriteAllBytesAsync(path, data, ct);
                    return new DownloadResult(setId, true, host, data.Length, path, null, hashes);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    lastError = $"{host}: {e.Message}";
                    logger.LogWarning("Download {Set} failed on {Host} (attempt {Attempt}): {Error}", setId, host, attempt, e.Message);
                    // Already retried with backoff by RateLimitHandler; go to the next mirror.
                    if (e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests }) break;
                    await Task.Delay(1500, ct);
                }
            }
        }
        return new DownloadResult(setId, false, null, 0, null, lastError, []);
    }

    /// <summary>
    /// Ensures the archive contains .osu files and returns the MD5 of each difficulty keyed by beatmap id.
    /// Those hashes are what collection.db stores, so collections match exactly what gets imported.
    /// </summary>
    public static Dictionary<int, string> ValidateOsz(byte[] data)
    {
        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        var hashes = new Dictionary<int, string>();
        int osuFiles = 0;
        foreach (var entry in zip.Entries.Where(e => e.Name.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)))
        {
            osuFiles++;
            using var ms = new MemoryStream();
            using (var s = entry.Open()) s.CopyTo(ms);
            byte[] raw = ms.ToArray();
            var m = BeatmapIdRegex().Match(System.Text.Encoding.UTF8.GetString(raw));
            if (m.Success && int.TryParse(m.Groups[1].Value, out int id) && id > 0)
                hashes[id] = Convert.ToHexStringLower(MD5.HashData(raw));
        }
        if (osuFiles == 0) throw new InvalidDataException("archive contains no .osu files");
        return hashes;
    }

    [GeneratedRegex(@"BeatmapID\s*:\s*(\d+)")]
    private static partial Regex BeatmapIdRegex();

    [GeneratedRegex("[\\\\/:*?\"<>|]")]
    private static partial Regex InvalidFileChars();
}
