using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NexusApp.Brokers.AliceBlue;

/// <summary>
/// AliceBlue ANT (v2) REST client.
/// Base URL: https://ant.aliceblueonline.com/rest/AliceBlueAPIService
/// Auth flow:
///   1. POST /customer/getAPIEncpkey { userId }              -> encKey
///   2. userData = SHA256(userId + apiKey + encKey)
///   3. POST /customer/getUserSID     { userId, userData }   -> sessionID
///   4. Authorization header: "Bearer {userId} {sessionID}"
/// Reference: https://v2api.aliceblueonline.com/introduction
/// </summary>
public class AliceBlueApiClient(HttpClient httpClient, ILogger<AliceBlueApiClient> logger, string apiUrl)
{
    private const string DefaultBaseUrl   = "https://ant.aliceblueonline.com/rest/AliceBlueAPIService";
    private const string EncKeyPath       = "/api/customer/getAPIEncpkey";
    private const string UserSidPath      = "/api/customer/getUserSID";
    private const string PlaceOrderPath   = "/api/placeOrder/executePlaceOrder";
    private const string OrderBookPath    = "/api/placeOrder/fetchOrderBook";
    private const string TradeBookPath    = "/api/placeOrder/fetchTradeBook";
    private const string PositionBookPath = "/api/positionAndHoldings/positionBook";
    private const string ScripQuotePath   = "/api/ScripDetails/getScripQuoteDetails";
    private const string CancelOrderPath  = "/api/placeOrder/cancelOrder";
    private const string ModifyOrderPath  = "/api/placeOrder/modifyOrder";
    private const string RmsLimitsPath    = "/api/limits/getRmsLimits";

    private readonly HttpClient _httpClient = httpClient;
    private readonly ILogger<AliceBlueApiClient> _logger = logger;
    private readonly string _baseUrl = string.IsNullOrWhiteSpace(apiUrl) ? DefaultBaseUrl : apiUrl.TrimEnd('/');

    private string? _apiKey;
    private string? _userId;
    private string? _sessionId;
    private DateTime _sessionExpiresAt;

    public bool IsAuthenticated => !string.IsNullOrEmpty(_sessionId) && DateTime.UtcNow < _sessionExpiresAt;
    public string? SessionId    => _sessionId;
    public string? UserId       => _userId;

    /// <summary>
    /// Pre-configures the API key (and optional URL override consumed at login).
    /// </summary>
    public void Configure(string apiKey, string? apiUrlOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
            _apiKey = apiKey;
        _pendingApiUrlOverride = string.IsNullOrWhiteSpace(apiUrlOverride) ? null : apiUrlOverride!.TrimEnd('/');
    }

    private string? _pendingApiUrlOverride;

    /// <summary>
    /// Performs the two-step AliceBlue session-key handshake and stores the session id.
    /// </summary>
    public async Task<bool> LoginAsync(string userId, string apiKey)
    {
        try
        {
            _userId = userId;
            _apiKey = string.IsNullOrWhiteSpace(apiKey) ? _apiKey : apiKey;
            var baseUrl = _pendingApiUrlOverride ?? _baseUrl;

            if (string.IsNullOrWhiteSpace(_userId) || string.IsNullOrWhiteSpace(_apiKey))
            {
                _logger.LogError("AliceBlue login missing userId or apiKey");
                return false;
            }

            // Step 1: request the per-session encryption key.
            using var encMsg = BuildRequest(HttpMethod.Post, $"{baseUrl}{EncKeyPath}", new { userId = _userId });
            var encResp = await _httpClient.SendAsync(encMsg);
            var encBody = await encResp.Content.ReadAsStringAsync();
            if (!encResp.IsSuccessStatusCode)
            {
                _logger.LogError("AliceBlue getEncKey failed {Status}: {Body}", (int)encResp.StatusCode, encBody);
                return false;
            }

            using var encDoc = JsonDocument.Parse(encBody);
            if (!encDoc.RootElement.TryGetProperty("encKey", out var encKeyEl) ||
                encKeyEl.ValueKind != JsonValueKind.String)
            {
                _logger.LogError("AliceBlue getEncKey returned no encKey: {Body}", encBody);
                return false;
            }
            var encKey = encKeyEl.GetString()!;

            // Step 2: userData = SHA256(userId + apiKey + encKey), exchange for session id.
            var userData = Sha256Hex(_userId + _apiKey + encKey);
            using var sidMsg = BuildRequest(HttpMethod.Post, $"{baseUrl}{UserSidPath}",
                new { userId = _userId, userData });
            var sidResp = await _httpClient.SendAsync(sidMsg);
            var sidBody = await sidResp.Content.ReadAsStringAsync();
            if (!sidResp.IsSuccessStatusCode)
            {
                _logger.LogError("AliceBlue getUserSID failed {Status}: {Body}", (int)sidResp.StatusCode, sidBody);
                return false;
            }

            using var sidDoc = JsonDocument.Parse(sidBody);
            var root = sidDoc.RootElement;
            var stat = root.TryGetProperty("stat", out var st) ? st.GetString() : null;
            if (!string.Equals(stat, "Ok", StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("sessionID", out var sidEl) ||
                sidEl.ValueKind != JsonValueKind.String)
            {
                var emsg = root.TryGetProperty("emsg", out var e) ? e.GetString() : null;
                _logger.LogError("AliceBlue getUserSID rejected: stat={Stat} emsg={Emsg} body={Body}",
                    stat, emsg, sidBody);
                return false;
            }

            _sessionId = sidEl.GetString();
            _sessionExpiresAt = DateTime.UtcNow.AddHours(12);
            _logger.LogInformation("AliceBlue login successful for {UserId}", _userId);
            return !string.IsNullOrEmpty(_sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during AliceBlue login");
            return false;
        }
    }

    public async Task<JsonElement?> GetOrderBookAsync()    => await PostSecureAsync(OrderBookPath, new { });
    public async Task<JsonElement?> GetTradeBookAsync()    => await PostSecureAsync(TradeBookPath, new { });
    public async Task<JsonElement?> GetPositionBookAsync() => await PostSecureAsync(PositionBookPath, new { ret = "NET" });

    /// <summary>
    /// Fetches RMS funds/margin limits for the account (idempotent read).
    /// </summary>
    public async Task<JsonElement?> GetRmsLimitsAsync() => await PostSecureAsync(RmsLimitsPath, new { });

    /// <summary>
    /// Fetches a live quote. Retried aggressively (idempotent read).
    /// </summary>
    public async Task<JsonElement?> GetScripQuoteAsync(string exchange, string token)
    {
        try
        {
            using var response = await SendWithRetryAsync(
                () => BuildSecureRequest(HttpMethod.Post, $"{_baseUrl}{ScripQuotePath}",
                    new { exch = exchange, symbol = token }),
                $"GetScripQuote({exchange}:{token})", allowAmbiguousRetry: true);
            if (!response.IsSuccessStatusCode)
                return null;
            return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting AliceBlue quote for {Exchange}:{Token}", exchange, token);
            return null;
        }
    }

    /// <summary>
    /// Places an order. NOT idempotent — only connection-level failures that
    /// provably never reached the exchange are retried (no duplicate-order risk).
    /// Terminal/ambiguous failures surface as JSON { stat=Not_Ok, httpStatus, emsg }.
    /// </summary>
    public async Task<JsonElement?> PlaceOrderAsync(object orderRequest)
    {
        try
        {
            using var response = await SendWithRetryAsync(
                () => BuildSecureRequest(HttpMethod.Post, $"{_baseUrl}{PlaceOrderPath}", orderRequest),
                "PlaceOrder", allowAmbiguousRetry: false);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("AliceBlue place order failed {StatusCode}: {Body}", response.StatusCode, body);
                var errorJson = JsonSerializer.Serialize(new
                {
                    stat = "Not_Ok",
                    httpStatus = (int)response.StatusCode,
                    emsg = body
                });
                return JsonSerializer.Deserialize<JsonElement>(errorJson);
            }
            return JsonSerializer.Deserialize<JsonElement>(body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error placing AliceBlue order");
            var errorJson = JsonSerializer.Serialize(new
            {
                stat = "Not_Ok",
                httpStatus = 500,
                emsg = ex.Message
            });
            return JsonSerializer.Deserialize<JsonElement>(errorJson);
        }
    }

    public async Task<(bool Success, string? Message)> CancelOrderAsync(string orderId, string exchange)
    {
        try
        {
            using var response = await SendWithRetryAsync(
                () => BuildSecureRequest(HttpMethod.Post, $"{_baseUrl}{CancelOrderPath}",
                    new { nestOrderNumber = orderId, exch = exchange }),
                $"CancelOrder({orderId})", allowAmbiguousRetry: true);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return (false, $"HTTP {(int)response.StatusCode}");
            return (IsStatOk(body), body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AliceBlue cancelOrder error for {OrderId}", orderId);
            return (false, ex.Message);
        }
    }

    public async Task<bool> ModifyOrderAsync(object modifyRequest)
    {
        try
        {
            using var response = await SendWithRetryAsync(
                () => BuildSecureRequest(HttpMethod.Post, $"{_baseUrl}{ModifyOrderPath}", modifyRequest),
                "ModifyOrder", allowAmbiguousRetry: false);
            var body = await response.Content.ReadAsStringAsync();
            return response.IsSuccessStatusCode && IsStatOk(body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AliceBlue modifyOrder error");
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<JsonElement?> PostSecureAsync(string path, object body)
    {
        try
        {
            using var response = await SendWithRetryAsync(
                () => BuildSecureRequest(HttpMethod.Post, $"{_baseUrl}{path}", body),
                $"POST {path}", allowAmbiguousRetry: true);
            if (!response.IsSuccessStatusCode)
                return null;
            return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling AliceBlue {Path}", path);
            return null;
        }
    }

    private static bool IsStatOk(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                    if (item.TryGetProperty("stat", out var s) &&
                        string.Equals(s.GetString(), "Ok", StringComparison.OrdinalIgnoreCase))
                        return true;
                return false;
            }
            return root.TryGetProperty("stat", out var st) &&
                   string.Equals(st.GetString(), "Ok", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string Sha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private HttpRequestMessage BuildSecureRequest(HttpMethod method, string url, object? body = null)
        => BuildRequest(method, url, body, authorize: true);

    private HttpRequestMessage BuildRequest(HttpMethod method, string url, object? body = null, bool authorize = false)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        if (authorize && !string.IsNullOrEmpty(_sessionId) && !string.IsNullOrEmpty(_userId))
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_userId} {_sessionId}");

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return req;
    }

    /// <summary>
    /// Sends an HTTP request with bounded retry-with-backoff for transient failures.
    /// A fresh <see cref="HttpRequestMessage"/> is built per attempt via
    /// <paramref name="requestFactory"/> (messages cannot be resent).
    /// </summary>
    /// <param name="allowAmbiguousRetry">
    /// <c>true</c> for idempotent reads (retry timeouts/5xx/429); <c>false</c> for
    /// non-idempotent writes such as order placement (retry connection failures only).
    /// </param>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        string operation,
        bool allowAmbiguousRetry,
        int maxAttempts = TransientRetry.DefaultMaxAttempts)
    {
        for (var attempt = 0; ; attempt++)
        {
            var isLastAttempt = attempt >= maxAttempts - 1;

            FailureKind kind;
            HttpResponseMessage? response = null;
            Exception? failure = null;

            using var msg = requestFactory();
            try
            {
                response = await _httpClient.SendAsync(msg);
                kind = TransientRetry.Classify(response.StatusCode);
                if (kind == FailureKind.Success)
                    return response;
            }
            catch (Exception ex)
            {
                failure = ex;
                kind = TransientRetry.Classify(ex);
            }

            var retryable = kind switch
            {
                FailureKind.ConnectFailure => true,
                FailureKind.Transient      => allowAmbiguousRetry,
                _                          => false
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
}
