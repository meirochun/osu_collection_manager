using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OsuCollectionManager.Services;

/// <summary>
/// Reads public osu.ppy.sh pages without an API key. Requests are serialized and spaced out
/// so bulk tag lookups don't hammer the site.
/// </summary>
public sealed partial class OsuWebClient(IHttpClientFactory factory)
{
    private HttpClient Http => factory.CreateClient("osuweb");

    private async Task<string> GetAsync(string url, bool json, CancellationToken ct)
    {
        // Pacing and 429 backoff live in RateLimitHandler.
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (json) req.Headers.Accept.ParseAdd("application/json");
        using var res = await Http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    public async Task<UserProfile> GetProfileAsync(string user, int topPlays, CancellationToken ct)
    {
        string html = await GetAsync($"https://osu.ppy.sh/users/{Uri.EscapeDataString(user)}/osu", false, ct);
        var m = InitialDataRegex().Match(html);
        if (!m.Success) throw new InvalidOperationException($"Profile '{user}' not found.");
        var data = JsonNode.Parse(WebUtility.HtmlDecode(m.Groups[1].Value))!;
        var u = data["user"]!;
        var stats = u["statistics"];

        var plays = new List<TopPlay>();
        for (int offset = 0; offset < topPlays; offset += 50)
        {
            int limit = Math.Min(50, topPlays - offset);
            var page = JsonNode.Parse(await GetAsync(
                $"https://osu.ppy.sh/users/{u["id"]}/scores/best?mode=osu&limit={limit}&offset={offset}", true, ct))!.AsArray();
            foreach (var s in page)
            {
                var b = s!["beatmap"]!;
                var bs = s["beatmapset"]!;
                var mods = s["mods"]!.AsArray().Select(x => x is JsonObject o ? (string?)o["acronym"] : (string?)x).Where(x => x is not "CL");
                plays.Add(new TopPlay(
                    (int)b["id"]!, (int)b["beatmapset_id"]!, (string)bs["artist"]!, (string)bs["title"]!, (string)b["version"]!,
                    (string)bs["creator"]!, (double?)s["pp"] ?? 0, (double)s["accuracy"]! * 100, string.Concat(mods),
                    (double)b["difficulty_rating"]!, (double)b["bpm"]!, (string?)b["checksum"] ?? ""));
            }
            if (page.Count < limit) break;
        }

        return new UserProfile((int)u["id"]!, (string)u["username"]!, (double?)stats?["pp"] ?? 0,
            (int?)stats?["global_rank"], (int?)stats?["country_rank"], (double?)stats?["hit_accuracy"] ?? 0, plays);
    }

    public async Task<SetTags?> GetSetTagsAsync(int setId, CancellationToken ct)
    {
        string html = await GetAsync($"https://osu.ppy.sh/beatmapsets/{setId}", false, ct);
        var m = BeatmapsetJsonRegex().Match(html);
        if (!m.Success) return null;
        var d = JsonNode.Parse(m.Groups[1].Value)!;

        var names = new Dictionary<int, string>();
        foreach (var t in d["related_tags"]?.AsArray() ?? [])
            names[(int)t!["id"]!] = (string)t["name"]!;

        var byBeatmap = new Dictionary<int, Dictionary<string, int>>();
        foreach (var b in d["beatmaps"]!.AsArray())
        {
            var tags = new Dictionary<string, int>();
            foreach (var t in b!["top_tag_ids"]?.AsArray() ?? [])
            {
                int id = (int)t!["tag_id"]!;
                tags[names.GetValueOrDefault(id, id.ToString())] = (int)t["count"]!;
            }
            byBeatmap[(int)b["id"]!] = tags;
        }
        return new SetTags(setId, (string?)d["status"] ?? "", (int?)d["favourite_count"] ?? 0, byBeatmap);
    }

    [GeneratedRegex("data-initial-data=\"([^\"]+)\"")]
    private static partial Regex InitialDataRegex();

    [GeneratedRegex("<script id=\"json-beatmapset\" type=\"application/json\">(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex BeatmapsetJsonRegex();
}
