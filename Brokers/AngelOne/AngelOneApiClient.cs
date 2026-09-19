using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NexusApp.Brokers.AngelOne;

/// <summary>
/// Angel One SmartAPI HTTP client.
/// Official production URL: https://apiconnect.angelone.in
/// Auth:    POST /rest/auth/angelbroking/user/v1/loginByPassword
/// Secured: /rest/secure/angelbroking/{service}/v1/{action}
/// Reference: github.com/angel-one/smartapi-python
/// </summary>
public class AngelOneApiClient(HttpClient httpClient, ILogger<AngelOneApiClient> logger, string apiUrl)
{
    // Correct production endpoints from official Angel One SDK
    private const string DefaultBaseUrl = "https://apiconnect.angelone.in";
    private const string PublicIpLookupUrl = "https://api.ipify.org";
    private const string LoginPath = "/rest/auth/angelbroking/user/v1/loginByPassword";
    private const string RefreshPath = "/rest/auth/angelbroking/jwt/v1/generateTokens";
    private const string ProfilePath = "/rest/secure/angelbroking/user/v1/getProfile";
    private const string PlaceOrderPath = "/rest/secure/angelbroking/order/v1/placeOrder";
    private const string ModifyOrderPath = "/rest/secure/angelbroking/order/v1/modifyOrder";
    private const string CancelOrderPath = "/rest/secure/angelbroking/order/v1/cancelOrder";
    private const string OrderBookPath = "/rest/secure/angelbroking/order/v1/getOrderBook";
    private const string TradeBookPath = "/rest/secure/angelbroking/order/v1/getTradeBook";
    private const string PositionPath = "/rest/secure/angelbroking/order/v1/getPosition";
    private const string LtpDataPath = "/rest/secure/angelbroking/order/v1/getLtpData";
    private const string SearchPath = "/rest/secure/angelbroking/order/v1/searchScrip";

    private readonly HttpClient _httpClient = httpClient;
    private readonly ILogger<AngelOneApiClient> _logger = logger;
    private readonly string _baseUrl = string.IsNullOrWhiteSpace(apiUrl) ? DefaultBaseUrl : apiUrl.TrimEnd('/');
    private string _activeBaseUrl = string.IsNullOrWhiteSpace(apiUrl) ? DefaultBaseUrl : apiUrl.TrimEnd('/');

    private string? _apiKey;
    private string? _jwtToken;
    private string? _refreshToken;
    private string? _feedToken;
    private string? _clientCode;
    private DateTime _tokenExpiresAt;

    // In-memory credential cache for emergency re-login when refresh-token flow fails.
    private string? _lastLoginClientId;
    private string? _lastLoginPassword;
    private string? _lastLoginTwoFactor;
    private string? _lastLoginApiUrl;

    // Serialises token refreshes so concurrent AG8001 ("Invalid Token") failures
    // trigger a single re-auth instead of a stampede of refresh calls.
    private readonly SemaphoreSlim _reauthLock = new(1, 1);

    // Proactively keeps every outbound call within Angel One's published per-endpoint
    // throttling limits, so we never trigger the 403 "exceeding access rate" rejection.
    private readonly AngelRateLimiter _rateLimiter = new(logger);

    // Angel publishes getPosition at 1 request/second. AngelRateLimiter enforces that
    // hard ceiling; this shared cache additionally collapses the several background
    // services that read the book so they don't each queue behind a 1/sec gate.
    private static readonly TimeSpan PositionBookMinInterval = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _positionBookLock = new(1, 1);
    private JsonElement? _positionBookCache;
    private DateTime _positionBookCachedAtUtc = DateTime.MinValue;
    private DateTime _rateLimitedUntilUtc = DateTime.MinValue;

    private static readonly string _localIp = GetLocalIp();
    private static readonly string _macAddr = GetMacAddress();
    private static string _publicIp = string.Empty;
    private static bool _publicIpResolved;

    /// <summary>
    /// Pre-configures API key and optional URL override before calling AuthenticateAsync via IBroker.
    /// Call this before IBroker.AuthenticateAsync so the broker picks up the right credentials.
    /// </summary>
    public void Configure(string apiKey, string? apiUrlOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
            _apiKey = apiKey;

        _pendingApiUrlOverride = string.IsNullOrWhiteSpace(apiUrlOverride) ? null : apiUrlOverride!.TrimEnd('/');
        _activeBaseUrl = _pendingApiUrlOverride ?? _baseUrl;
    }

    // Pending URL override set via Configure() — consumed by LoginAsync when no explicit override passed
    private string? _pendingApiUrlOverride;

    public bool IsAuthenticated => !string.IsNullOrEmpty(_jwtToken) && DateTime.UtcNow < _tokenExpiresAt;
    public string? JwtToken => _jwtToken;
    public string? FeedToken => _feedToken;
    public string? ClientCode => _clientCode;
    public string? ApiKey => _apiKey;

    /// <summary>
    /// Authenticates with Angel One SmartAPI.
    /// Sends all required headers: X-PrivateKey, X-UserType, X-SourceID,
    /// X-ClientLocalIP, X-ClientPublicIP (real public IP via ipify.org), X-MACAddress.
    /// </summary>
    public async Task<bool> LoginAsync(string clientId, string password, string twoFactorCode,
                                        string? apiKey = null, string? apiUrlOverride = null)
    {
        try
        {
            await EnsurePublicIpAsync();
            var totpCode = ResolveTotpCode(twoFactorCode);
            if (string.IsNullOrWhiteSpace(totpCode))
            {
                _logger.LogWarning("Angel One login rejected: missing/invalid TOTP value.");
                return false;
            }

            var baseUrl = !string.IsNullOrWhiteSpace(apiUrlOverride)
                ? apiUrlOverride!.TrimEnd('/')
                : (_pendingApiUrlOverride ?? _baseUrl);
            _activeBaseUrl = baseUrl;
            _apiKey = apiKey ?? _apiKey;

            // Cache latest login inputs for fallback re-login if JWT refresh fails later.
            _lastLoginClientId = clientId;
            _lastLoginPassword = password;
            _lastLoginTwoFactor = twoFactorCode;
            _lastLoginApiUrl = baseUrl;

            var requestBody = new
            {
                clientcode = clientId,
                password,
                totp = totpCode
            };

            using var msg = BuildRequest(HttpMethod.Post, $"{baseUrl}{LoginPath}", requestBody);
            var response = await _httpClient.SendAsync(msg);

            var body = await response.Content.ReadAsStringAsync();
            _logger.LogDebug("Login response {StatusCode}: {Body}", (int)response.StatusCode, body);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Angel One login failed: {StatusCode} — {Body}", response.StatusCode, body);
                return false;
            }

            var json = JsonSerializer.Deserialize<JsonElement>(body);

            if (json.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.False)
            {
                var errMsg = json.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error";
                _logger.LogError("Angel One login status=false: {Message}", errMsg);
                return false;
            }

            if (json.TryGetProperty("data", out var data) && data.ValueKind != JsonValueKind.Null)
            {
                _jwtToken = data.TryGetString("jwtToken");
                _refreshToken = data.TryGetString("refreshToken");
                _feedToken = data.TryGetString("feedToken");
                _clientCode = clientId;
                _tokenExpiresAt = DateTime.UtcNow.AddHours(23);
                _logger.LogInformation("Angel One login successful for {ClientId} (feedToken={HasFeed})", clientId, !string.IsNullOrEmpty(_feedToken));
                return !string.IsNullOrEmpty(_jwtToken);
            }

            _logger.LogError("Angel One login: unexpected response — {Body}", body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Angel One login");
            return false;
        }
    }

    public async Task<bool> RefreshTokenAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_refreshToken))
            {
                _logger.LogWarning("Token refresh skipped: refresh token is missing.");
                return false;
            }

            var requestBody = new { refreshToken = _refreshToken };
            using var msg = BuildRequest(HttpMethod.Post, $"{_activeBaseUrl}{RefreshPath}", requestBody, authorize: true);
            var response = await _httpClient.SendAsync(msg);

            if (!response.IsSuccessStatusCode)
            {
                var failBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Token refresh failed HTTP {StatusCode}: {Body}", (int)response.StatusCode, Truncate(failBody, 400));
                return false;
            }

            var raw = await response.Content.ReadAsStringAsync();
            var json = JsonSerializer.Deserialize<JsonElement>(raw);
            if (json.TryGetProperty("data", out var data))
            {
                var newToken = data.TryGetString("jwtToken");
                if (!string.IsNullOrEmpty(newToken))
                {
                    _jwtToken = newToken;
                    _tokenExpiresAt = DateTime.UtcNow.AddHours(23);
                    _logger.LogInformation("Token refreshed successfully");
                    return true;
                }
            }

            _logger.LogWarning("Token refresh returned no jwtToken. Body: {Body}", Truncate(raw, 400));
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing token");
            return false;
        }
    }

    public async Task<JsonElement?> GetProfileAsync()
        => await GetSecureAsync(ProfilePath);

    public async Task<JsonElement?> GetLtpAsync(string exchange, string tradingsymbol, string symboltoken)
    {
        try
        {
            var requestBody = new { exchange, tradingsymbol, symboltoken };
            using var response = await SendWithRetryAsync(
                () => BuildRequest(HttpMethod.Post, $"{_activeBaseUrl}{LtpDataPath}", requestBody, authorize: true),
                $"GetLtp({tradingsymbol})", allowAmbiguousRetry: true);
            if (!response.IsSuccessStatusCode)
                return null;
            return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting LTP for {Symbol}", tradingsymbol);
            return null;
        }
    }

    public async Task<JsonElement?> SearchScripAsync(string exchange, string searchscrip)
    {
        try
        {
            var requestBody = new { exchange, searchscrip };
            using var response = await SendWithRetryAsync(
                () => BuildRequest(HttpMethod.Post, $"{_activeBaseUrl}{SearchPath}", requestBody, authorize: true),
                $"SearchScrip({searchscrip})", allowAmbiguousRetry: true);
            if (!response.IsSuccessStatusCode)
                return null;
            return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching scrip: {Symbol}", searchscrip);
            return null;
        }
    }

    public async Task<JsonElement?> PlaceOrderAsync(object orderRequest)
    {
        try
        {
            // Order placement is NOT idempotent. Only retry connection-level
            // failures that provably never reached the exchange; a timeout or
            // 5xx after send is ambiguous and must not be retried (duplicate risk).
            // Stale-token (AG8001) recovery is safe here: such responses are
            // rejections that never reached the exchange (no orderid), so the
            // single re-auth + replay inside SendWithRetryAsync cannot double-fill.
            using var response = await SendWithRetryAsync(
                () => BuildRequest(HttpMethod.Post, $"{_activeBaseUrl}{PlaceOrderPath}", orderRequest, authorize: true),
                "PlaceOrder", allowAmbiguousRetry: false);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Place order failed {StatusCode}: {Body}", response.StatusCode, body);
                var errorJson = JsonSerializer.Serialize(new
                {
                    status = false,
                    httpStatus = (int)response.StatusCode,
                    errorMessage = body
                });
                return JsonSerializer.Deserialize<JsonElement>(errorJson);
            }
            return JsonSerializer.Deserialize<JsonElement>(body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error placing order");
            var errorJson = JsonSerializer.Serialize(new
            {
                status = false,
                httpStatus = 500,
                errorMessage = ex.Message
            });
            return JsonSerializer.Deserialize<JsonElement>(errorJson);
        }
    }

    /// <summary>
    /// True when an Angel One response body reports an invalid/expired session token
    /// (errorCode AG8001 or message "Invalid Token") despite an HTTP 200.
    /// </summary>
    private static bool IsInvalidTokenResponse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(body);
            if (json.ValueKind != JsonValueKind.Object)
                return false;

            if (json.TryGetProperty("errorCode", out var code)
                && string.Equals(code.GetString(), "AG8001", StringComparison.OrdinalIgnoreCase))
                return true;

            if (json.TryGetProperty("message", out var msg)
                && string.Equals(msg.GetString(), "Invalid Token", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
            // Not JSON we understand — treat as not-an-invalid-token response.
        }
        return false;
    }

    /// <summary>
    /// Ensures a valid session token after an AG8001 ("Invalid Token") response.
    /// Serialised via <see cref="_reauthLock"/> so concurrent failures cause a
    /// single refresh; late arrivals see the freshly-minted token and skip re-auth.
    /// </summary>
    private async Task<bool> EnsureFreshTokenAsync(string? tokenAtFailure)
    {
        await _reauthLock.WaitAsync();
        try
        {
            // Another caller already refreshed while we waited — reuse their token.
            if (!string.Equals(tokenAtFailure, _jwtToken, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(_jwtToken))
            {
                return true;
            }

            if (await RefreshTokenAsync())
                return true;

            // Refresh token can expire server-side; fall back to full login using
            // the latest successful login credentials cached in-memory.
            if (string.IsNullOrWhiteSpace(_lastLoginClientId) ||
                string.IsNullOrWhiteSpace(_lastLoginPassword) ||
                string.IsNullOrWhiteSpace(_lastLoginTwoFactor))
            {
                _logger.LogError("Session re-login failed: cached login credentials unavailable.");
                return false;
            }

            _logger.LogWarning("Refresh-token flow failed; attempting full Angel One re-login.");
            var reloginOk = await LoginAsync(
                _lastLoginClientId,
                _lastLoginPassword,
                _lastLoginTwoFactor,
                _apiKey,
                _lastLoginApiUrl);

            if (!reloginOk)
            {
                _logger.LogError("Full re-login failed after refresh-token failure.");
                return false;
            }

            _logger.LogInformation("Session recovered via full re-login.");
            return true;
        }
        finally
        {
            _reauthLock.Release();
        }
    }

    public async Task<JsonElement?> GetOrderBookAsync() => await GetSecureAsync(OrderBookPath);
    public async Task<JsonElement?> GetPositionBookAsync()
    {
        await _positionBookLock.WaitAsync();
        try
        {
            var now = DateTime.UtcNow;
            var cacheIsFresh = _positionBookCache.HasValue &&
                               now - _positionBookCachedAtUtc < PositionBookMinInterval;

            if (cacheIsFresh || (now < _rateLimitedUntilUtc && _positionBookCache.HasValue))
                return _positionBookCache;

            var result = await GetSecureAsync(PositionPath);
            if (result is null)
                return _positionBookCache;

            if (IsRateLimitedResponse(result.Value))
            {
                _rateLimitedUntilUtc = DateTime.UtcNow.Add(PositionBookMinInterval);
                _logger.LogWarning(
                    "Angel position book is rate limited; serving cached payload for the next {Seconds}s.",
                    PositionBookMinInterval.TotalSeconds);
                return _positionBookCache ?? result;
            }

            _positionBookCache = result;
            _positionBookCachedAtUtc = DateTime.UtcNow;
            return result;
        }
        finally
        {
            _positionBookLock.Release();
        }
    }
    public async Task<JsonElement?> GetTradeBookAsync() => await GetSecureAsync(TradeBookPath);

    public async Task<bool> ModifyOrderAsync(object modifyRequest)
    {
        using var response = await SendWithRetryAsync(
            () => BuildRequest(HttpMethod.Post, $"{_activeBaseUrl}{ModifyOrderPath}", modifyRequest, authorize: true),
            "ModifyOrder", allowAmbiguousRetry: false);
        return response.IsSuccessStatusCode;
    }

    public async Task<(bool Success, string? Message)> CancelOrderAsync(object cancelRequest)
    {
        using var response = await SendWithRetryAsync(
            () => BuildRequest(HttpMethod.Post, $"{_activeBaseUrl}{CancelOrderPath}", cancelRequest, authorize: true),
            "CancelOrder", allowAmbiguousRetry: false);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("cancelOrder HTTP {Status}: {Body}", (int)response.StatusCode, Truncate(body, 500));
            return (false, $"HTTP {(int)response.StatusCode}: {Truncate(body, 200)}");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("status", out var st) &&
                     (st.ValueKind == JsonValueKind.True ||
                      (st.ValueKind == JsonValueKind.String && st.GetString()!.Equals("true", StringComparison.OrdinalIgnoreCase)));
            var apiMsg = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            var errCode = root.TryGetProperty("errorcode", out var e) ? e.GetString() : null;

            if (!ok)
            {
                _logger.LogWarning("cancelOrder rejected by Angel: message={Msg} errorcode={Code} body={Body}",
                    apiMsg, errCode, Truncate(body, 500));
            }
            return (ok, string.IsNullOrWhiteSpace(apiMsg) ? errCode : apiMsg);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "cancelOrder unparseable response: {Body}", Truncate(body, 500));
            return (false, "Unparseable broker response");
        }
    }

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? string.Empty : (s!.Length <= max ? s : s[..max] + "…");

    /// <summary>
    /// True when an Angel response indicates request-rate throttling rather than an
    /// authentication or data failure.
    /// </summary>
    private static bool IsRateLimitBody(string? body)
        => !string.IsNullOrWhiteSpace(body) &&
           body.Contains("exceeding access rate", StringComparison.OrdinalIgnoreCase);

    private static bool IsRateLimitedResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        if (root.TryGetProperty("errorMessage", out var error) && error.ValueKind == JsonValueKind.String)
            return IsRateLimitBody(error.GetString());

        if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            return IsRateLimitBody(message.GetString());

        return false;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<JsonElement?> GetSecureAsync(string path)
    {
        try
        {
            using var response = await SendWithRetryAsync(
                () => BuildRequest(HttpMethod.Get, $"{_activeBaseUrl}{path}", body: null, authorize: true),
                $"GET {path}", allowAmbiguousRetry: true);

            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Secure API call {Path} failed HTTP {StatusCode}: {Body}",
                    path, (int)response.StatusCode, Truncate(body, 500));

                var errorJson = JsonSerializer.Serialize(new
                {
                    status = false,
                    httpStatus = (int)response.StatusCode,
                    errorMessage = string.IsNullOrWhiteSpace(body)
                        ? $"HTTP {(int)response.StatusCode} {response.StatusCode}"
                        : body
                });
                return JsonSerializer.Deserialize<JsonElement>(errorJson);
            }

            return JsonSerializer.Deserialize<JsonElement>(body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Sends an HTTP request with bounded retry-with-backoff for transient failures.
    /// A fresh <see cref="HttpRequestMessage"/> is built per attempt via
    /// <paramref name="requestFactory"/> (messages cannot be resent).
    /// </summary>
    /// <param name="allowAmbiguousRetry">
    /// When <c>true</c> (idempotent reads), ambiguous transients — timeouts and
    /// HTTP 5xx/429 after the request was sent — are also retried. When
    /// <c>false</c> (non-idempotent writes such as order placement), only
    /// connection-level failures that provably never reached the exchange are
    /// retried, to avoid duplicate submissions.
    /// </param>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        string operation,
        bool allowAmbiguousRetry,
        int maxAttempts = TransientRetry.DefaultMaxAttempts)
    {
        var reauthAttempted = false;
        for (int attempt = 0; ; attempt++)
        {
            var isLastAttempt = attempt >= maxAttempts - 1;

            FailureKind kind;
            HttpResponseMessage? response = null;
            Exception? failure = null;

            // Capture the token this attempt will send, so re-auth can detect
            // whether another caller already refreshed concurrently.
            var tokenSent = _jwtToken;

            using var msg = requestFactory();
            var authorized = msg.Headers.Contains("Authorization");

            // Wait for a slot in the endpoint's published quota before sending.
            if (msg.RequestUri is { } requestUri)
                await _rateLimiter.WaitAsync(requestUri.ToString());

            try
            {
                response = await _httpClient.SendAsync(msg);
                kind = TransientRetry.Classify(response.StatusCode);
                if (kind == FailureKind.Success)
                {
                    // Angel returns HTTP 200 with errorCode AG8001 ("Invalid Token")
                    // when the REST JWT has gone stale. Re-authenticate once and replay.
                    // Buffering the body here lets callers still read it afterwards.
                    if (authorized && !reauthAttempted)
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        if (IsInvalidTokenResponse(body))
                        {
                            reauthAttempted = true;
                            _logger.LogWarning(
                                "{Operation} rejected with Invalid Token (AG8001); refreshing session and replaying once.",
                                operation);
                            response.Dispose();
                            if (await EnsureFreshTokenAsync(tokenSent))
                            {
                                attempt--; // replay doesn't count against transient budget
                                continue;
                            }
                            _logger.LogError("{Operation}: token refresh failed after Invalid Token.", operation);
                            throw new InvalidOperationException("Angel One session expired and could not be refreshed.");
                        }
                    }
                    return response;
                }

                // Some Angel endpoints return 401/403 for an expired session instead of
                // HTTP 200 + AG8001. Recover once via refresh/re-login and replay.
                if (authorized &&
                    (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden))
                {
                    var rejectBody = await response.Content.ReadAsStringAsync();

                    if (IsRateLimitBody(rejectBody))
                    {
                        // Rate limiting is not an auth failure; re-login would make it worse.
                        kind = FailureKind.Transient;

                        // Angel's counter is ahead of ours (other sessions, clock skew),
                        // so pause this endpoint before the retry burns another slot.
                        if (msg.RequestUri is { } throttledUri)
                            _rateLimiter.ReportThrottled(throttledUri.ToString(), TimeSpan.FromSeconds(2));

                        _logger.LogWarning(
                            "{Operation} rate limited by Angel (HTTP {StatusCode}): {Body}",
                            operation, (int)response.StatusCode, Truncate(rejectBody, 300));
                    }
                    else if (!reauthAttempted)
                    {
                        reauthAttempted = true;
                        _logger.LogWarning(
                            "{Operation} returned HTTP {StatusCode}; attempting one session refresh and replay. Body={Body}",
                            operation, (int)response.StatusCode, Truncate(rejectBody, 500));
                        response.Dispose();

                        if (await EnsureFreshTokenAsync(tokenSent))
                        {
                            attempt--; // replay doesn't count against transient budget
                            continue;
                        }

                        throw new InvalidOperationException(
                            "Angel One request rejected and session refresh failed.");
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                kind = TransientRetry.Classify(ex);
            }

            // Decide whether this failure kind is retryable for this operation.
            var retryable = kind switch
            {
                FailureKind.ConnectFailure => true,
                FailureKind.Transient => allowAmbiguousRetry,
                _ => false
            };

            if (!retryable || isLastAttempt)
            {
                if (failure is not null)
                {
                    if (retryable)
                        _logger.LogWarning(failure,
                            "{Operation} failed after {Attempts} attempt(s) ({Kind}); giving up",
                            operation, attempt + 1, kind);
                    throw failure;
                }
                return response!;
            }

            response?.Dispose();
            var delay = TransientRetry.BackoffFor(attempt);
            _logger.LogWarning(
                "{Operation} transient failure ({Kind}) on attempt {Attempt}/{Max}; retrying in {Delay}ms",
                operation, kind, attempt + 1, maxAttempts, (int)delay.TotalMilliseconds);
            await Task.Delay(delay);
        }
    }

    /// <summary>
    /// Builds request with all required Angel One headers.
    /// </summary>
    private HttpRequestMessage BuildRequest(HttpMethod method, string url, object? body = null, bool authorize = false)
    {
        var req = new HttpRequestMessage(method, url);

        if (!string.IsNullOrWhiteSpace(_apiKey))
            req.Headers.TryAddWithoutValidation("X-PrivateKey", _apiKey);

        req.Headers.TryAddWithoutValidation("X-UserType", "USER");
        req.Headers.TryAddWithoutValidation("X-SourceID", "WEB");
        req.Headers.TryAddWithoutValidation("X-ClientLocalIP", _localIp);
        req.Headers.TryAddWithoutValidation("X-ClientPublicIP", string.IsNullOrEmpty(_publicIp) ? _localIp : _publicIp);
        req.Headers.TryAddWithoutValidation("X-MACAddress", _macAddr);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        if (authorize && !string.IsNullOrEmpty(_jwtToken))
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_jwtToken}");

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return req;
    }

    /// <summary>
    /// Fetches the machine's real public IP via ipify.org.
    /// Angel One SDK does the same (see official Python SDK source).
    /// </summary>
    private async Task EnsurePublicIpAsync()
    {
        if (_publicIpResolved)
            return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ip = await _httpClient.GetStringAsync(PublicIpLookupUrl, cts.Token);
            _publicIp = ip.Trim();
            _logger.LogDebug("Resolved public IP: {Ip}", _publicIp);
        }
        catch
        {
            _publicIp = _localIp;
            _logger.LogWarning("Could not resolve public IP — using local IP: {Ip}", _localIp);
        }
        finally
        {
            _publicIpResolved = true;
        }
    }

    private static string GetLocalIp()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(addr.Address))
                        return addr.Address.ToString();
                }
            }
        }
        catch { /* best-effort */ }
        return "127.0.0.1";
    }

    private static string GetMacAddress()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                var mac = ni.GetPhysicalAddress()?.ToString();
                if (!string.IsNullOrEmpty(mac) && mac != "000000000000")
                    return mac;
            }
        }
        catch { /* best-effort */ }
        return "000000000000";
    }

    private static string ResolveTotpCode(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var normalized = input.Trim().Replace(" ", string.Empty).ToUpperInvariant();
        var isCode = normalized.Length == 6 && normalized.All(char.IsDigit);
        if (isCode)
            return normalized;

        return TryComputeTotpFromBase32Secret(normalized, out var code)
            ? code
            : string.Empty;
    }

    private static bool TryComputeTotpFromBase32Secret(string secret, out string code)
    {
        code = string.Empty;
        try
        {
            var key = DecodeBase32(secret);
            if (key.Length == 0)
                return false;

            var timestep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
            Span<byte> counter = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(counter, timestep);

            using var hmac = new HMACSHA1(key);
            var hash = hmac.ComputeHash(counter.ToArray());
            var offset = hash[^1] & 0x0F;
            var binary =
                ((hash[offset] & 0x7F) << 24) |
                ((hash[offset + 1] & 0xFF) << 16) |
                ((hash[offset + 2] & 0xFF) << 8) |
                (hash[offset + 3] & 0xFF);

            code = (binary % 1_000_000).ToString("D6");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] DecodeBase32(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return [];

        var cleaned = input.Trim().TrimEnd('=').ToUpperInvariant();
        var bytes = new List<byte>((cleaned.Length * 5) / 8);

        var buffer = 0;
        var bitsLeft = 0;
        foreach (var c in cleaned)
        {
            var val = c switch
            {
                >= 'A' and <= 'Z' => c - 'A',
                >= '2' and <= '7' => c - '2' + 26,
                _ => -1
            };
            if (val < 0)
                throw new FormatException("Invalid Base32 character.");

            buffer = (buffer << 5) | val;
            bitsLeft += 5;

            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                bytes.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }

        return [.. bytes];
    }
}

public static class JsonElementExtensions
{
    public static string? TryGetString(this JsonElement el, string propertyName)
        => el.TryGetProperty(propertyName, out var p) ? p.GetString() : null;
}

public static class HttpContentExtensions
{
    public static async Task<T> ReadAsAsync<T>(this HttpContent content)
    {
        var json = await content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json) ?? throw new InvalidOperationException("Failed to deserialize response");
    }
}