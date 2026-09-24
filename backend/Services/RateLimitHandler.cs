using System.Collections.Concurrent;
using System.Net;

namespace OsuCollectionManager.Services;

/// <summary>
/// Per-host pacing plus 429 handling. Requests to one host are spaced out and capped in concurrency;
/// when a host answers 429 (or 503 with Retry-After) every caller for that host backs off, honoring
/// Retry-After when present, and the request is retried a few times before the response is surfaced.
/// State is static so all named clients share the same view of each host.
/// </summary>
public sealed class RateLimitHandler(IConfiguration config, ILogger<RateLimitHandler> logger) : DelegatingHandler
{
    private const int MaxRetries = 4;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    private static readonly ConcurrentDictionary<string, HostState> Hosts = new();

    private sealed class HostState(int concurrency, TimeSpan interval)
    {
        public readonly SemaphoreSlim Slots = new(concurrency, concurrency);
        public readonly TimeSpan Interval = interval;
        public readonly object Lock = new();
        public DateTime NextStart = DateTime.MinValue;
        public DateTime BlockedUntil = DateTime.MinValue;
        public int Strikes;
    }

    private HostState StateFor(string host) => Hosts.GetOrAdd(host, h =>
    {
        var s = config.GetSection($"RateLimits:{h}");
        var d = config.GetSection("RateLimits:Default");
        int conc = s.GetValue<int?>("MaxConcurrency") ?? d.GetValue<int?>("MaxConcurrency") ?? 2;
        int ms = s.GetValue<int?>("MinIntervalMs") ?? d.GetValue<int?>("MinIntervalMs") ?? 500;
        return new HostState(Math.Max(1, conc), TimeSpan.FromMilliseconds(Math.Max(0, ms)));
    });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string host = request.RequestUri!.Host;
        var state = StateFor(host);
        bool canRetry = request.Method == HttpMethod.Get && request.Content is null;

        for (int attempt = 0; ; attempt++)
        {
            await state.Slots.WaitAsync(ct);
            HttpResponseMessage res;
            try
            {
                // Reserve a start time under the lock, then sleep outside it.
                DateTime start;
                lock (state.Lock)
                {
                    start = DateTime.UtcNow;
                    if (state.NextStart > start) start = state.NextStart;
                    if (state.BlockedUntil > start) start = state.BlockedUntil;
                    state.NextStart = start + state.Interval;
                }
                var wait = start - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);

                res = await base.SendAsync(attempt == 0 || !canRetry ? request : Clone(request), ct);
            }
            finally
            {
                state.Slots.Release();
            }

            bool limited = res.StatusCode == HttpStatusCode.TooManyRequests
                           || (res.StatusCode == HttpStatusCode.ServiceUnavailable && res.Headers.RetryAfter is not null);
            if (!limited)
            {
                lock (state.Lock) state.Strikes = 0;
                return res;
            }

            TimeSpan delay;
            lock (state.Lock)
            {
                state.Strikes++;
                delay = res.Headers.RetryAfter switch
                {
                    { Delta: { } d } => d,
                    { Date: { } dt } => dt - DateTimeOffset.UtcNow,
                    _ => TimeSpan.FromSeconds(Math.Pow(2, state.Strikes)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500)),
                };
                if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
                if (delay > MaxBackoff) delay = MaxBackoff;
                var until = DateTime.UtcNow + delay;
                if (until > state.BlockedUntil) state.BlockedUntil = until;
            }

            if (!canRetry || attempt >= MaxRetries) return res;
            logger.LogWarning("{Host} returned {Status}; backing off {Delay:F1}s (retry {N}/{Max})",
                host, (int)res.StatusCode, delay.TotalSeconds, attempt + 1, MaxRetries);
            res.Dispose();
        }
    }

    private static HttpRequestMessage Clone(HttpRequestMessage r)
    {
        var c = new HttpRequestMessage(r.Method, r.RequestUri) { Version = r.Version, VersionPolicy = r.VersionPolicy };
        foreach (var h in r.Headers) c.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return c;
    }
}
