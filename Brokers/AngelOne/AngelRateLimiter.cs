using System.Collections.Concurrent;

namespace NexusApp.Brokers.AngelOne;

/// <summary>
/// Proactive client-side throttle for Angel One SmartAPI, mirroring the published
/// per-endpoint limits (https://smartapi.angelbroking.com/docs/RateLimit).
///
/// Angel rejects excess traffic with HTTP 403 "Access denied because of exceeding
/// access rate", which is indistinguishable from a session failure at the transport
/// level, so it is far cheaper to never exceed the limit than to recover from it.
/// Each endpoint keeps sliding windows for its second / minute / hour quotas and
/// callers simply await a slot.
/// </summary>
public sealed class AngelRateLimiter
{
    /// <summary>Published quota for one endpoint. <c>null</c> means "not applicable".</summary>
    private sealed record Limits(int? PerSecond, int? PerMinute, int? PerHour);

    /// <summary>
    /// Published limits keyed by request path. Paths are matched case-insensitively
    /// and by suffix so template endpoints (e.g. /details/{GuiOrderID}) still match.
    /// </summary>
    private static readonly Dictionary<string, Limits> Published = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/rest/auth/angelbroking/user/v1/loginByPassword"] = new(1, null, null),
        ["/rest/auth/angelbroking/jwt/v1/generateTokens"] = new(1, null, 1000),
        ["/rest/secure/angelbroking/user/v1/getProfile"] = new(3, null, 1000),
        ["/rest/secure/angelbroking/user/v1/logout"] = new(1, null, null),
        ["/rest/secure/angelbroking/user/v1/getRMS"] = new(2, null, null),
        ["/rest/secure/angelbroking/order/v1/placeOrder"] = new(9, 500, 1000),
        ["/rest/secure/angelbroking/order/v1/modifyOrder"] = new(9, 500, 1000),
        ["/rest/secure/angelbroking/order/v1/cancelOrder"] = new(9, 500, 1000),
        ["/rest/secure/angelbroking/order/v1/getOrderBook"] = new(1, null, null),
        ["/rest/secure/angelbroking/order/v1/getLtpData"] = new(10, 500, 5000),
        ["/rest/secure/angelbroking/order/v1/getPosition"] = new(1, null, null),
        ["/rest/secure/angelbroking/order/v1/getTradeBook"] = new(1, null, null),
        ["/rest/secure/angelbroking/order/v1/convertPosition"] = new(10, 500, 5000),
        ["/rest/secure/angelbroking/order/v1/searchScrip"] = new(1, null, null),
        ["/rest/secure/angelbroking/order/v1/details"] = new(10, 500, 5000),
        ["/rest/secure/angelbroking/portfolio/v1/getHolding"] = new(1, null, null),
        ["/rest/secure/angelbroking/portfolio/v1/getAllHolding"] = new(1, null, null),
        ["/rest/secure/angelbroking/market/v1/quote"] = new(10, 500, 5000),
        ["/rest/secure/angelbroking/margin/v1/batch"] = new(10, 500, 5000),
        ["/rest/secure/angelbroking/gtt/v1/createRule"] = new(9, 500, 5000),
        ["/rest/secure/angelbroking/gtt/v1/modifyRule"] = new(9, 500, 5000),
        ["/rest/secure/angelbroking/gtt/v1/cancelRule"] = new(9, 500, 5000),
        ["/rest/secure/angelbroking/gtt/v1/ruleDetails"] = new(10, 500, 5000),
        ["/rest/secure/angelbroking/gtt/v1/ruleList"] = new(10, 500, 5000),
        ["/rest/secure/angelbroking/historical/v1/getCandleData"] = new(3, 150, 5000),
        ["/rest/secure/angelbroking/marketData/v1/optionGreek"] = new(1, null, null)
    };

    /// <summary>Conservative default for any endpoint not listed above.</summary>
    private static readonly Limits Fallback = new(1, null, null);

    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, EndpointGate> _gates =
        new(StringComparer.OrdinalIgnoreCase);

    public AngelRateLimiter(ILogger logger) => _logger = logger;

    /// <summary>
    /// Blocks until the caller may issue a request to <paramref name="pathOrUrl"/>
    /// without breaching the published quota, then records the request.
    /// </summary>
    public Task WaitAsync(string pathOrUrl, CancellationToken ct = default)
    {
        var (key, limits) = Resolve(pathOrUrl);
        var gate = _gates.GetOrAdd(key, _ => new EndpointGate(key, limits, _logger));
        return gate.WaitAsync(ct);
    }

    /// <summary>
    /// Applies a server-signalled cooldown after Angel reported throttling, so the
    /// next caller backs off rather than immediately retrying into the same wall.
    /// </summary>
    public void ReportThrottled(string pathOrUrl, TimeSpan cooldown)
    {
        var (key, limits) = Resolve(pathOrUrl);
        var gate = _gates.GetOrAdd(key, _ => new EndpointGate(key, limits, _logger));
        gate.ApplyCooldown(cooldown);
        _logger.LogWarning(
            "Angel reported rate limiting on {Endpoint}; pausing that endpoint for {Seconds:N1}s",
            key, cooldown.TotalSeconds);
    }

    private static (string Key, Limits Limits) Resolve(string pathOrUrl)
    {
        var path = pathOrUrl;

        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri))
            path = uri.AbsolutePath;

        // Angel's docs contain a double slash in one path; normalise so lookups match.
        path = path.Replace("//", "/", StringComparison.Ordinal);

        foreach (var entry in Published)
        {
            if (path.EndsWith(entry.Key, StringComparison.OrdinalIgnoreCase) ||
                path.Contains(entry.Key, StringComparison.OrdinalIgnoreCase))
            {
                return (entry.Key, entry.Value);
            }
        }

        return (path, Fallback);
    }

    /// <summary>
    /// Sliding-window gate for a single endpoint. Requests are serialised so the
    /// windows stay consistent under concurrent callers.
    /// </summary>
    private sealed class EndpointGate(string endpoint, Limits limits, ILogger logger)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Queue<DateTime> _second = new();
        private readonly Queue<DateTime> _minute = new();
        private readonly Queue<DateTime> _hour = new();
        private DateTime _cooldownUntilUtc = DateTime.MinValue;

        public void ApplyCooldown(TimeSpan cooldown)
        {
            var until = DateTime.UtcNow.Add(cooldown);
            if (until > _cooldownUntilUtc)
                _cooldownUntilUtc = until;
        }

        public async Task WaitAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                while (true)
                {
                    var now = DateTime.UtcNow;
                    var wait = TimeSpan.Zero;

                    if (now < _cooldownUntilUtc)
                        wait = _cooldownUntilUtc - now;

                    wait = Max(wait, WindowWait(_second, limits.PerSecond, TimeSpan.FromSeconds(1), now));
                    wait = Max(wait, WindowWait(_minute, limits.PerMinute, TimeSpan.FromMinutes(1), now));
                    wait = Max(wait, WindowWait(_hour, limits.PerHour, TimeSpan.FromHours(1), now));

                    if (wait <= TimeSpan.Zero)
                    {
                        var stamp = DateTime.UtcNow;
                        if (limits.PerSecond.HasValue) _second.Enqueue(stamp);
                        if (limits.PerMinute.HasValue) _minute.Enqueue(stamp);
                        if (limits.PerHour.HasValue) _hour.Enqueue(stamp);
                        return;
                    }

                    if (wait > TimeSpan.FromSeconds(2))
                    {
                        logger.LogInformation(
                            "Throttling {Endpoint} for {Seconds:N1}s to stay within Angel One's published rate limit",
                            endpoint, wait.TotalSeconds);
                    }

                    // Small pad so we wake up just after the window rolls over.
                    await Task.Delay(wait + TimeSpan.FromMilliseconds(25), ct);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Trims expired stamps and returns how long to wait for a free slot in this window.
        /// </summary>
        private static TimeSpan WindowWait(Queue<DateTime> window, int? limit, TimeSpan span, DateTime now)
        {
            if (limit is not { } max)
                return TimeSpan.Zero;

            while (window.Count > 0 && now - window.Peek() >= span)
                window.Dequeue();

            if (window.Count < max)
                return TimeSpan.Zero;

            return span - (now - window.Peek());
        }

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    }
}
