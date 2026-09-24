using System.Globalization;

namespace NexusApp.Helpers;

/// <summary>
/// Identifies and parses expiration dates from Indian exchange option trading symbols
/// (NSE, BSE, MCX) to detect expired contracts and avoid redundant broker queries.
/// </summary>
public static class ContractExpiryHelper
{
    private static readonly string[] KnownUnderlyings =
    [
        // Commodity underlyings (longest-first to avoid prefix collisions like GOLD vs GOLDM)
        "CRUDEOILM", "NATGASMINI", "GOLDM", "SILVERM", "SILVERMIC",
        "CRUDEOIL", "NATURALGAS", "GOLD", "SILVER",
        // Equity / Index underlyings
        "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "SENSEX", "BANKEX", "NIFTY"
    ];

    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JAN"] = 1, ["FEB"] = 2, ["MAR"] = 3, ["APR"] = 4,
        ["MAY"] = 5, ["JUN"] = 6, ["JUL"] = 7, ["AUG"] = 8,
        ["SEP"] = 9, ["OCT"] = 10, ["NOV"] = 11, ["DEC"] = 12
    };

    /// <summary>
    /// Attempts to extract the expiry date from an option tradingsymbol.
    /// Handles:
    /// 1. Weekly/Monthly with 3-letter month: e.g. NIFTY17SEP2624000CE (DDMMMYY), BANKNIFTY26JUL57000PE (YYMMM)
    /// 2. BSE weekly format: e.g. SENSEX2691773900PE (YY M DD, where 26=2026, 9=Sept, 17=17th)
    /// </summary>
    public static bool TryGetExpiryDate(string? tradingSymbol, out DateTime expiryDate)
    {
        expiryDate = default;
        if (string.IsNullOrWhiteSpace(tradingSymbol))
            return false;

        var s = tradingSymbol.Trim().ToUpperInvariant();
        if (s.Length < 8)
            return false;

        // Must end with CE or PE to be an option
        if (!s.EndsWith("CE", StringComparison.Ordinal) && !s.EndsWith("PE", StringComparison.Ordinal))
            return false;

        var body = s[..^2];

        // Match underlying prefix
        string? underlying = null;
        foreach (var u in KnownUnderlyings)
        {
            if (body.StartsWith(u, StringComparison.Ordinal))
            {
                underlying = u;
                break;
            }
        }

        if (underlying == null)
            return false;

        var mid = body[underlying.Length..];
        if (mid.Length < 5)
            return false;

        // Format 1: 3-letter month MMM
        int monthIdx = -1;
        for (var i = 0; i + 3 <= mid.Length; i++)
        {
            var candidate = mid.Substring(i, 3);
            if (Months.ContainsKey(candidate))
            {
                monthIdx = i;
                break;
            }
        }

        if (monthIdx >= 0)
        {
            var monthStr = mid.Substring(monthIdx, 3);
            var month = Months[monthStr];

            if (monthIdx == 2)
            {
                // DD MMM YY (e.g. 17SEP2624000)
                if (int.TryParse(mid[..2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var day) &&
                    mid.Length >= 7 &&
                    int.TryParse(mid.AsSpan(5, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var yy))
                {
                    var year = 2000 + yy;
                    expiryDate = new DateTime(year, month, Math.Clamp(day, 1, DateTime.DaysInMonth(year, month)), 0, 0, 0, DateTimeKind.Unspecified);
                    return true;
                }
            }
            else if (monthIdx > 0)
            {
                // YY MMM (e.g. 26SEP57000)
                if (int.TryParse(mid[..monthIdx], NumberStyles.Integer, CultureInfo.InvariantCulture, out var yy))
                {
                    var year = 2000 + yy;
                    // For monthly options where exact day is not in string, use last day of month.
                    expiryDate = new DateTime(year, month, DateTime.DaysInMonth(year, month), 23, 59, 59, DateTimeKind.Unspecified);
                    return true;
                }
            }
        }

        // Format 2: Compact weekly format: YY M DD (e.g. SENSEX26917... -> 26 9 17)
        // Mid starts with 2-digit year (e.g. 26)
        // 3rd char is month: '1'-'9' for Jan-Sep, 'O' for Oct, 'N' for Nov, 'D' for Dec
        // 4th and 5th chars are 2-digit day: '17'
        if (mid.Length >= 5 &&
            int.TryParse(mid.AsSpan(0, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var yearPart))
        {
            var mChar = mid[2];
            int month = mChar switch
            {
                >= '1' and <= '9' => mChar - '0',
                'O' or 'o' or '0' => 10,
                'N' or 'n' => 11,
                'D' or 'd' => 12,
                _ => 0
            };

            if (month > 0 &&
                int.TryParse(mid.AsSpan(3, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dayPart))
            {
                var year = 2000 + yearPart;
                if (dayPart >= 1 && dayPart <= DateTime.DaysInMonth(year, month))
                {
                    expiryDate = new DateTime(year, month, dayPart, 0, 0, 0, DateTimeKind.Unspecified);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if the symbol is an option contract whose expiration date
    /// has strictly passed relative to today's date in Indian Standard Time (IST).
    /// </summary>
    public static bool IsExpiredOptionSymbol(string? tradingSymbol)
    {
        if (TryGetExpiryDate(tradingSymbol, out var expiryDate))
        {
            var todayIst = DateTimeExtensions.IstToday();
            return expiryDate.Date < todayIst;
        }

        return false;
    }
}
