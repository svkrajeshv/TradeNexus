using NexusApp.Helpers;
using NexusApp.Interfaces;
using NexusApp.Models;
using System.Text.RegularExpressions;

namespace NexusApp.Parser;

/// <summary>
/// Base class implementing the generic Telegram signal parsing pipeline shared
/// by all channel strategies. Channel-specific parsers derive from this class and
/// override only the hooks (<see cref="CanHandle"/>, <see cref="Priority"/>,
/// <see cref="ShouldSkip"/>, <see cref="RequiresActivation"/>) they need to change.
/// </summary>
public abstract class SignalParserBase(ILogger logger, IServiceScopeFactory? scopeFactory = null) : IChannelSignalParser
{
    private readonly ILogger _logger = logger;

    // Regex patterns for parsing signals
    protected const string ActionPattern = @"(BUY|SELL|BUYSELECT|BUY SELECT)";
    protected const string IndexPattern = @"(NIFTY|BANKNIFTY|FINNIFTY|MIDCPNIFTY|SENSEX|BANKEX)";
    protected const string StrikePattern = @"(\d{4,5})";
    protected const string OptionTypePattern = @"(CE|PE|CALL|PUT)";
    // Matches: ABOVE 👉 100 = 95, ABOVE: 115, ABOVE 115
    // [^\d]*? lazily skips any non-digit chars (emoji / arrows / punctuation) up to the first number.
    protected const string PricePattern = @"ABOVE[^\d]*?(\d+(?:\.\d+)?)";
    // Fallback: BUY NNN or BUY NNN/MMM++ — used when no ABOVE keyword is present.
    protected const string BuyPricePattern = @"(?:^|\s)BUY\s+(\d+(?:\.\d+)?)";
    // Matches: TARGET 👉 150/180/200✅  |  Target 180 220 300+  |  TARGETS: 200/210  |  TGT 150
    // [^\dA-Z]* skips separators/emoji but NOT letters, so an empty target
    // ("TARGET :-  / SL :- 165") cannot run past the "SL" label and capture the
    // stop-loss number as a target. Non-numeric targets fall back to DefaultTargetPoints.
    protected const string TargetPattern = @"(?:TARGETS?|TGT)[^\dA-Z]*(\d[\d\s/+.]*)";
    // Matches: SL: 180  |  STOPLOSS 👉 #paid group  |  SL :- PAID
    // [^\dA-Z]*? skips separators/emoji up to the first number or the PAID keyword.
    protected const string StopLossPattern = @"(?:SL|STOPLOSS)[^\dA-Z]*?(\d+|PAID)";

    /// <summary>
    /// Default SL buffer (in points) applied when the signal doesn't quote a
    /// numeric SL (e.g. "SL :- PAID" or SL missing). This prevents SL == Entry
    /// which would trigger an immediate stop-out at execution.
    /// </summary>
    protected const decimal DefaultSlBufferPoints = 50m;
    protected const string ExpiryPattern = @"(\d{1,2})\s*(JANUARY|FEBRUARY|MARCH|APRIL|MAY|JUNE|JULY|AUGUST|SEPTEMBER|OCTOBER|NOVEMBER|DECEMBER|JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC)";

    /// <summary>
    /// Effective default SL buffer (points) used when a signal has no numeric SL.
    /// Defaults to <see cref="DefaultSlBufferPoints"/>; channel parsers may override
    /// (e.g. from settings) without affecting other channels.
    /// </summary>
    protected decimal DefaultSlPoints { get; set; } = DefaultSlBufferPoints;

    /// <summary>
    /// Default target buffer (points) applied when a signal has no numeric target
    /// (e.g. "TGT paid" / "premium group" / missing). Loaded from settings
    /// (DefaultTargetPoints) for every channel; defaults to 20.
    /// </summary>
    protected decimal DefaultTargetPoints { get; set; } = 20m;

    private readonly IServiceScopeFactory? _scopeFactory = scopeFactory;

    /// <summary>
    /// Commodity underlyings, built from the single source of truth so this can never
    /// drift from the instrument-master filter or the segment resolver.
    /// </summary>
    private static readonly string CommodityIndexPattern =
        $"({string.Join("|", MarketSegments.CommodityUnderlyings.OrderByDescending(c => c.Length))})";

    /// <summary>
    /// Commodity strikes span a far wider range than index strikes (NATURALGAS trades
    /// near 200, GOLD above 100000), so <see cref="StrikePattern"/>'s <c>(\d{4,5})</c>
    /// cannot cover them. Requires a trailing CE/PE so it cannot capture an entry price
    /// or an expiry day by mistake.
    /// </summary>
    private const string CommodityStrikePattern = @"(\d{2,7})\s*(?:CE|PE|CALL|PUT)";

    /// <summary>
    /// Set during <see cref="ParseAsync"/> from the <c>Mcx.Enabled</c> master toggle.
    /// Fails closed: stays false when the setting cannot be read.
    /// </summary>
    private bool _mcxEnabled;

    /// <summary>
    /// True when the parsed underlying belongs to the commodity segment. Channels are
    /// resolved by name, but a single channel may post both equity and commodity calls,
    /// so the segment can only be known per-message.
    /// </summary>
    protected static bool IsCommoditySignal(ParsedSignal signal) => MarketSegments.IsCommodity(signal.Index);

    // ---- Channel strategy hooks (overridable) --------------------------------

    /// <inheritdoc />
    public virtual int Priority => 0;

    /// <inheritdoc />
    public virtual bool CanHandle(string? channelName) => true;

    /// <inheritdoc />
    public virtual bool ShouldSkip(string? message) => false;

    /// <inheritdoc />
    public virtual bool RequiresActivation => false;

    /// <inheritdoc />
    public virtual bool IsActivationMessage(string? message) => false;

    // ---- Parsing pipeline ----------------------------------------------------

    public virtual async Task<ParsedSignal?> ParseAsync(string message, long telegramMessageId, DateTime telegramTimestamp)
    {
        await LoadDefaultPointsAsync();
        return await Task.Run(() => Parse(message, telegramMessageId, telegramTimestamp));
    }

    /// <summary>
    /// Loads the configurable default Target/SL points (in pts) from settings so that
    /// signals lacking a numeric target/SL fall back to the user-configured values.
    /// Applies to every channel. Silently keeps the built-in defaults (20 / 50) if the
    /// settings can't be read or no scope factory was provided.
    /// </summary>
    private async Task LoadDefaultPointsAsync()
    {
        if (_scopeFactory is null) return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

            var target = await settings.GetSettingAsync<decimal?>("DefaultTargetPoints");
            if (target is > 0) DefaultTargetPoints = target.Value;

            var sl = await settings.GetSettingAsync<decimal?>("DefaultStopLossPoints");
            if (sl is > 0) DefaultSlPoints = sl.Value;

            _mcxEnabled = await settings.GetSettingAsync<bool?>("Mcx.Enabled") ?? false;
        }
        catch
        {
            // Keep built-in defaults on any failure. _mcxEnabled stays false (fails closed).
        }
    }

    /// <summary>
    /// Parse a message for unit testing (uses DateTime.UtcNow as timestamp)
    /// </summary>
    public ParsedSignal? Parse(string message)
    {
        return Parse(message, 0, DateTime.UtcNow);
    }

    protected virtual ParsedSignal? Parse(string message, long telegramMessageId, DateTime telegramTimestamp)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                _logger.LogWarning("Received empty message");
                return null;
            }

            var normalized = NormalizeMessage(message);
            var signal = new ParsedSignal
            {
                OriginalMessage = message,
                SignalTime = telegramTimestamp
            };

            // Parse action (BUY/SELL)
            if (!ParseAction(normalized, signal))
            {
                signal.ValidationErrors = "Could not parse BUY/SELL action";
                return signal;
            }

            // Parse index (NIFTY, BANKNIFTY, CRUDEOIL, etc.)
            if (!ParseIndex(normalized, signal))
            {
                signal.ValidationErrors = "Could not parse index name";
                return signal;
            }

            // Commodity master toggle. Applied here rather than per-channel because a
            // channel may post both equity and commodity calls, so the segment is only
            // known once the underlying has been parsed.
            if (IsCommoditySignal(signal) && !_mcxEnabled)
            {
                _logger.LogInformation(
                    "Commodity trading is disabled; ignoring {Index} signal message {MessageId}.",
                    signal.Index, telegramMessageId);
                return null;
            }

            // Parse strike price
            if (!ParseStrike(normalized, signal))
            {
                signal.ValidationErrors = "Could not parse strike price";
                return signal;
            }

            // Parse option type (CE/PE)
            if (!ParseOptionType(normalized, signal))
            {
                signal.ValidationErrors = "Could not parse option type (CE/PE)";
                return signal;
            }

            // Parse entry price
            if (!ParseEntryPrice(normalized, signal))
            {
                signal.ValidationErrors = "Could not parse entry price";
                return signal;
            }

            // Parse targets
            if (!ParseTargets(normalized, signal))
            {
                signal.ValidationErrors = "Could not parse targets";
                return signal;
            }

            // Parse stop loss
            if (!ParseStopLoss(normalized, signal))
            {
                signal.ValidationErrors = "Could not parse stop loss";
                return signal;
            }

            // Parse expiry date (optional)
            if (!ParseExpiryDate(normalized, signal))
            {
                _logger.LogInformation("No expiry date parsed in signal; nearest weekly expiry will be used by the trading engine.");
            }

            signal.IsValid = true;
            _logger.LogInformation("Successfully parsed signal: {Index} {Strike} {OptionType} - {Action}",
                signal.Index, signal.Strike, signal.OptionType, signal.Action);

            return signal;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing signal");
            return null;
        }
    }

    protected static string NormalizeMessage(string message)
    {
        // Normalize whitespace and case
        var normalized = Regex.Replace(message, @"\s+", " ");
        normalized = Regex.Replace(normalized, @"\n\s*", " ");
        return normalized.ToUpperInvariant();
    }

    protected virtual bool ParseAction(string message, ParsedSignal signal)
    {
        var match = Regex.Match(message, ActionPattern);
        if (match.Success)
        {
            signal.Action = match.Groups[1].Value.StartsWith("BUY") ? SignalAction.Buy : SignalAction.Sell;
            return true;
        }
        return false;
    }

    protected virtual bool ParseIndex(string message, ParsedSignal signal)
    {
        var match = Regex.Match(message, IndexPattern);
        if (match.Success)
        {
            signal.Index = match.Groups[1].Value;
            return true;
        }

        // Fallback: the message may be a commodity call on an otherwise equity channel.
        match = Regex.Match(message, CommodityIndexPattern, RegexOptions.IgnoreCase);
        if (match.Success)
        {
            signal.Index = match.Groups[1].Value.ToUpperInvariant();
            return true;
        }

        return false;
    }

    protected virtual bool ParseStrike(string message, ParsedSignal signal)
    {
        // Commodity strikes fall outside the 4-5 digit index range, so match on the
        // strike that is explicitly followed by CE/PE instead.
        if (IsCommoditySignal(signal))
        {
            var commodity = Regex.Match(message, CommodityStrikePattern, RegexOptions.IgnoreCase);
            if (commodity.Success && decimal.TryParse(commodity.Groups[1].Value, out var commodityStrike) && commodityStrike > 0)
            {
                signal.Strike = commodityStrike;
                return true;
            }
            return false;
        }

        var match = Regex.Match(message, StrikePattern);
        if (match.Success && decimal.TryParse(match.Groups[1].Value, out var strike))
        {
            signal.Strike = strike;
            return true;
        }
        return false;
    }

    protected static bool ParseOptionType(string message, ParsedSignal signal)
    {
        var match = Regex.Match(message, OptionTypePattern);
        if (match.Success)
        {
            var type = match.Groups[1].Value;
            signal.OptionType = type.StartsWith("C", StringComparison.OrdinalIgnoreCase) ? OptionType.Ce : OptionType.Pe;
            return true;
        }
        return false;
    }

    protected virtual bool ParseEntryPrice(string message, ParsedSignal signal)
    {
        // Primary: ABOVE keyword (handles emoji arrows between keyword and number)
        var match = Regex.Match(message, PricePattern, RegexOptions.IgnoreCase);
        if (match.Success && decimal.TryParse(match.Groups[1].Value, out var price) && price > 0)
        {
            signal.EntryPrice = price;
            return true;
        }

        // Fallback: "Buy 110/120++" style — take the first number after BUY
        match = Regex.Match(message, BuyPricePattern, RegexOptions.IgnoreCase);
        if (match.Success && decimal.TryParse(match.Groups[1].Value, out price) && price > 0)
        {
            signal.EntryPrice = price;
            return true;
        }

        return false;
    }

    protected virtual bool ParseTargets(string message, ParsedSignal signal)
    {
        var match = Regex.Match(message, TargetPattern, RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var raw = match.Groups[1].Value;
            // Split on slash or whitespace, strip non-numeric trailing chars (+ ✅ etc.)
            var parts = Regex.Split(raw, @"[/\s]+");
            foreach (var part in parts)
            {
                // Strip trailing non-digit characters (e.g. '+', '✅')
                var cleaned = Regex.Match(part.Trim(), @"^\d+(?:\.\d+)?");
                if (cleaned.Success && decimal.TryParse(cleaned.Value, out var targetPrice))
                    signal.Targets.Add(targetPrice);
            }
            if (signal.Targets.Count > 0)
                return true;
        }

        // No numeric target quoted (e.g. "TGT paid" / "premium group" / missing):
        // apply the configurable default target relative to entry so the signal stays valid.
        if (signal.EntryPrice > 0)
        {
            var target = signal.Action == SignalAction.Buy
                ? signal.EntryPrice + DefaultTargetPoints
                : Math.Max(0.05m, signal.EntryPrice - DefaultTargetPoints);
            signal.Targets.Add(target);
            return true;
        }

        return false;
    }

    protected virtual bool ParseStopLoss(string message, ParsedSignal signal)
    {
        var match = Regex.Match(message, StopLossPattern, RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var slValue = match.Groups[1].Value.Trim();

            // "PAID" or non-numeric → apply the default buffer below entry so
            // SL is never equal to Entry (which would immediately trigger).
            if (slValue.Equals("PAID", StringComparison.OrdinalIgnoreCase))
            {
                signal.StopLoss = ApplyDefaultSl(signal.EntryPrice);
                return true;
            }

            if (decimal.TryParse(slValue, out var sl))
            {
                // Protect against signals that mistakenly quote SL == Entry.
                signal.StopLoss = sl == signal.EntryPrice
                    ? ApplyDefaultSl(signal.EntryPrice)
                    : sl;
                return true;
            }
        }

        // If SL is completely absent (e.g. "#paid group" not matched above), apply default buffer.
        if (signal.EntryPrice > 0)
        {
            signal.StopLoss = ApplyDefaultSl(signal.EntryPrice);
            return true;
        }

        return false;
    }

    protected virtual decimal ApplyDefaultSl(decimal entry)
    {
        if (entry <= 0) return entry;
        var sl = entry - DefaultSlPoints;
        return sl > 0 ? sl : Math.Max(0.05m, entry * 0.5m);
    }

    protected static bool ParseExpiryDate(string message, ParsedSignal signal)
    {
        var match = Regex.Match(message, ExpiryPattern);
        if (match.Success)
        {
            var day = int.Parse(match.Groups[1].Value);
            var month = ParseMonth(match.Groups[2].Value);

            if (month > 0)
            {
                var istToday = DateTimeExtensions.IstToday();
                var year = istToday.Year;

                // If month is in past, use next year
                if (month < istToday.Month)
                    year++;

                try
                {
                    signal.ExpiryDate = new DateTime(year, month, day);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }
        return false;
    }

    protected static int ParseMonth(string month)
    {
        return month.ToUpperInvariant() switch
        {
            "JANUARY" or "JAN" => 1,
            "FEBRUARY" or "FEB" => 2,
            "MARCH" or "MAR" => 3,
            "APRIL" or "APR" => 4,
            "MAY" => 5,
            "JUNE" or "JUN" => 6,
            "JULY" or "JUL" => 7,
            "AUGUST" or "AUG" => 8,
            "SEPTEMBER" or "SEP" => 9,
            "OCTOBER" or "OCT" => 10,
            "NOVEMBER" or "NOV" => 11,
            "DECEMBER" or "DEC" => 12,
            _ => 0
        };
    }
}
