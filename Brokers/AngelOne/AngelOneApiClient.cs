using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NexusApp.Brokers.AngelOne;

/// <summary>
/// Angel One SmartAPI HTTP client.
/// Official production URL: https://apiconnect.angelone.in
/// Auth:    POST /rest/auth/angelbroking/user/v1/loginByPassword
/// Secured: /rest/secure/angelbroking/{service}/v1/{action}
/// Reference: github.com/angel-one/smartapi-python
/// </summary>
public class AngelOneApiClient
{
    // Correct production endpoints from official Angel One SDK
    private const string DefaultBaseUrl  = "https://apiconnect.angelone.in";
    private const string LoginPath       = "/rest/auth/angelbroking/user/v1/loginByPassword";
    private const string RefreshPath     = "/rest/auth/angelbroking/jwt/v1/generateTokens";
    private const string ProfilePath     = "/rest/secure/angelbroking/user/v1/getProfile";
    private const string PlaceOrderPath  = "/rest/secure/angelbroking/order/v1/placeOrder";
    private const string ModifyOrderPath = "/rest/secure/angelbroking/order/v1/modifyOrder";
    private const string CancelOrderPath = "/rest/secure/angelbroking/order/v1/cancelOrder";
    private const string OrderBookPath   = "/rest/secure/angelbroking/order/v1/getOrderBook";
    private const string TradeBookPath   = "/rest/secure/angelbroking/order/v1/getTradeBook";
    private const string PositionPath    = "/rest/secure/angelbroking/order/v1/getPosition";
    private const string LtpDataPath     = "/rest/secure/angelbroking/order/v1/getLtpData";
    private const string SearchPath      = "/rest/secure/angelbroking/order/v1/searchScrip";

    private readonly HttpClient _httpClient;
    private readonly ILogger<AngelOneApiClient> _logger;
    private readonly string _baseUrl;

    private string? _apiKey;
    private string? _jwtToken;
    private string? _refreshToken;
    private string? _feedToken;
    private string? _clientCode;
    private DateTime _tokenExpiresAt;

    private static readonly string _localIp = GetLocalIp();
    private static readonly string _macAddr = GetMacAddress();
    private static string _publicIp = string.Empty;
    private static bool _publicIpResolved;

    public AngelOneApiClient(HttpClient httpClient, ILogger<AngelOneApiClient> logger, string apiUrl)
    {
        _httpClient = httpClient;
        _logger     = logger;
        _baseUrl    = string.IsNullOrWhiteSpace(apiUrl) ? DefaultBaseUrl : apiUrl.TrimEnd('/');
    }

    /// <summary>
    /// Pre-configures API key and optional URL override before calling AuthenticateAsync via IBroker.
    /// Call this before IBroker.AuthenticateAsync so the broker picks up the right credentials.
    /// </summary>
    public void Configure(string apiKey, string? apiUrlOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
            _apiKey = apiKey;
        // Note: _baseUrl is readonly; apiUrlOverride is passed at login time via LoginAsync
        _pendingApiUrlOverride = string.IsNullOrWhiteSpace(apiUrlOverride) ? null : apiUrlOverride!.TrimEnd('/');
    }

    // Pending URL override set via Configure() — consumed by LoginAsync when no explicit override passed
    private string? _pendingApiUrlOverride;

    public bool IsAuthenticated => !string.IsNullOrEmpty(_jwtToken) && DateTime.UtcNow < _tokenExpiresAt;
    public string? JwtToken     => _jwtToken;
    public string? FeedToken    => _feedToken;
    public string? ClientCode   => _clientCode;
    public string? ApiKey       => _apiKey;

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
            _apiKey = apiKey ?? _apiKey;

            var requestBody = new
            {
                clientcode = clientId,
                password,
                totp = totpCode
            };

            using var msg = BuildRequest(HttpMethod.Post, $"{baseUrl}{LoginPath}", requestBody);
            var response  = await _httpClient.SendAsync(msg);

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
                _jwtToken     = data.TryGetString("jwtToken");
                _refreshToken = data.TryGetString("refreshToken");
                _feedToken    = data.TryGetString("feedToken");
                _clientCode   = clientId;
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
                return false;

            var requestBody = new { refreshToken = _refreshToken };
            using var msg = BuildRequest(HttpMethod.Post, $"{_baseUrl}{RefreshPath}", requestBody, authorize: true);
            var response  = await _httpClient.SendAsync(msg);

            if (!response.IsSuccessStatusCode)
                return false;

            var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            if (json.TryGetProperty("data", out var data))
            {
                var newToken = data.TryGetString("jwtToken");
                if (!string.IsNullOrEmpty(newToken))
                {
                    _jwtToken       = newToken;
                    _tokenExpiresAt = DateTime.UtcNow.AddHours(23);
                    _logger.LogInformation("Token refreshed successfully");
                    return true;
                }
            }
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
            using var msg = BuildRequest(HttpMethod.Post, $"{_baseUrl}{LtpDataPath}", requestBody, authorize: true);
            var response  = await _httpClient.SendAsync(msg);
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
            using var msg = BuildRequest(HttpMethod.Post, $"{_baseUrl}{SearchPath}", requestBody, authorize: true);
            var response  = await _httpClient.SendAsync(msg);
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
            using var msg = BuildRequest(HttpMethod.Post, $"{_baseUrl}{PlaceOrderPath}", orderRequest, authorize: true);
            var response  = await _httpClient.SendAsync(msg);
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

    public async Task<JsonElement?> GetOrderBookAsync()    => await GetSecureAsync(OrderBookPath);
    public async Task<JsonElement?> GetPositionBookAsync() => await GetSecureAsync(PositionPath);
    public async Task<JsonElement?> GetTradeBookAsync()    => await GetSecureAsync(TradeBookPath);

    public async Task<bool> ModifyOrderAsync(object modifyRequest)
    {
        using var msg = BuildRequest(HttpMethod.Post, $"{_baseUrl}{ModifyOrderPath}", modifyRequest, authorize: true);
        var response  = await _httpClient.SendAsync(msg);
        return response.IsSuccessStatusCode;
    }

    public async Task<(bool Success, string? Message)> CancelOrderAsync(object cancelRequest)
    {
        using var msg = BuildRequest(HttpMethod.Post, $"{_baseUrl}{CancelOrderPath}", cancelRequest, authorize: true);
        var response = await _httpClient.SendAsync(msg);
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

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<JsonElement?> GetSecureAsync(string path)
    {
        try
        {
            using var msg = BuildRequest(HttpMethod.Get, $"{_baseUrl}{path}", body: null, authorize: true);
            var response  = await _httpClient.SendAsync(msg);
            if (!response.IsSuccessStatusCode)
                return null;
            return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling {Path}", path);
            return null;
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

        req.Headers.TryAddWithoutValidation("X-UserType",       "USER");
        req.Headers.TryAddWithoutValidation("X-SourceID",       "WEB");
        req.Headers.TryAddWithoutValidation("X-ClientLocalIP",  _localIp);
        req.Headers.TryAddWithoutValidation("X-ClientPublicIP", string.IsNullOrEmpty(_publicIp) ? _localIp : _publicIp);
        req.Headers.TryAddWithoutValidation("X-MACAddress",     _macAddr);
        req.Headers.TryAddWithoutValidation("Accept",           "application/json");

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
            using var cts      = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ip             = await _httpClient.GetStringAsync("https://api.ipify.org", cts.Token);
            _publicIp          = ip.Trim();
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
            var offset = hash[hash.Length - 1] & 0x0F;
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
            return Array.Empty<byte>();

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

        return bytes.ToArray();
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