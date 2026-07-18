using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NexusApp.Interfaces;
using NexusApp.Models;

namespace NexusApp.Parser;

/// <summary>
/// Parses trading signals from Telegram messages using regex patterns
/// </summary>
public class SignalParser : ISignalParser
{
    private readonly ILogger<SignalParser> _logger;

    // Regex patterns for parsing signals
    private const string ActionPattern = @"(BUY|SELL|BUYSELECT|BUY SELECT)";
    private const string IndexPattern = @"(NIFTY|BANKNIFTY|FINNIFTY|MIDCPNIFTY|SENSEX|BANKEX)";
    private const string StrikePattern = @"(\d{4,5})";
    private const string OptionTypePattern = @"(CE|PE|CALL|PUT)";
    private const string PricePattern = @"ABOVE\s+(\d+(?:\.\d+)?)";
    private const string TargetPattern = @"TARGET\s*:-\s*([\d\s/]+)";
    private const string StopLossPattern = @"SL\s*:-\s*([\d]+|PAID)";

    /// <summary>
    /// Default SL buffer (in points) applied when the signal doesn't quote a
    /// numeric SL (e.g. "SL :- PAID" or SL missing). This prevents SL == Entry
    /// which would trigger an immediate stop-out at execution.
    /// </summary>
    private const decimal DefaultSlBufferPoints = 50m;
    private const string ExpiryPattern = @"(\d{1,2})\s*(JANUARY|FEBRUARY|MARCH|APRIL|MAY|JUNE|JULY|AUGUST|SEPTEMBER|OCTOBER|NOVEMBER|DECEMBER|JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC)";

    public SignalParser(ILogger<SignalParser> logger)
    {
        _logger = logger;
    }

    public async Task<ParsedSignal?> ParseAsync(string message, long telegramMessageId, DateTime telegramTimestamp)
    {
        return await Task.Run(() => Parse(message, telegramMessageId, telegramTimestamp));
    }

    /// <summary>
    /// Parse a message for unit testing (uses DateTime.UtcNow as timestamp)
    /// </summary>
    public ParsedSignal? Parse(string message)
    {
        return Parse(message, 0, DateTime.UtcNow);
    }

    private ParsedSignal? Parse(string message, long telegramMessageId, DateTime telegramTimestamp)
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

            // Parse index (NIFTY, BANKNIFTY, etc.)
            if (!ParseIndex(normalized, signal))
            {
                signal.ValidationErrors = "Could not parse index name";
                return signal;
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

            // Parse expiry date
            if (!ParseExpiryDate(normalized, signal))
            {
                signal.ValidationErrors = "Could not parse expiry date";
                return signal;
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

    private static string NormalizeMessage(string message)
    {
        // Normalize whitespace and case
        var normalized = Regex.Replace(message, @"\s+", " ");
        normalized = Regex.Replace(normalized, @"\n\s*", " ");
        return normalized.ToUpperInvariant();
    }

    private static bool ParseAction(string message, ParsedSignal signal)
    {
        var match = Regex.Match(message, ActionPattern);
        if (match.Success)
        {
            signal.Action = match.Groups[1].Value.StartsWith("BUY") ? SignalAction.Buy : SignalAction.Sell;
            return true;
        }
        return false;
    }

    private static bool ParseIndex(string message, ParsedSignal signal)
    {
        var match = Regex.Match(message, IndexPattern);
        if (match.Success)
        {
            signal.Index = match.Groups[1].Value;
            return true;
        }
        return false;
    }

    private static bool ParseStrike(string message, ParsedSignal signal)
    {
        var match = Regex.Match(message, StrikePattern);
        if (match.Success && decimal.TryParse(match.Groups[1].Value, out var strike))
        {
            signal.Strike = strike;
            return true;
        }
        return false;
    }

    private static bool ParseOptionType(string message, ParsedSignal signal)
    {
        var match = Regex.Match(message, OptionTypePattern);
        if (match.Success)
        {
            var type = match.Groups[1].Value;
            signal.OptionType = type.StartsWith("C") ? OptionType.Ce : OptionType.Pe;
            return true;
        }
        return false;
    }

    private static bool ParseEntryPrice(string message, ParsedSignal signal)
    {
        var match = Regex.Match(message, PricePattern);
        if (match.Success && decimal.TryParse(match.Groups[1].Value, out var price))
        {
            signal.EntryPrice = price;
            return true;
        }
        return false;
    }

    private static bool ParseTargets(string message, ParsedSignal signal)
    {
        var match = Regex.Match(message, TargetPattern);
        if (match.Success)
        {
            var targets = match.Groups[1].Value.Split('/');
            foreach (var target in targets)
            {
                if (decimal.TryParse(target.Trim(), out var targetPrice))
                {
                    signal.Targets.Add(targetPrice);
                }
            }
            return signal.Targets.Count > 0;
        }
        return false;
    }

    private static bool ParseStopLoss(string message, ParsedSignal signal)
    {
        var match = Regex.Match(message, StopLossPattern);
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
        return false;
    }

    private static decimal ApplyDefaultSl(decimal entry)
    {
        if (entry <= 0) return entry;
        var sl = entry - DefaultSlBufferPoints;
        return sl > 0 ? sl : Math.Max(0.05m, entry * 0.5m);
    }

    private static bool ParseExpiryDate(string message, ParsedSignal signal)
    {
        var match = Regex.Match(message, ExpiryPattern);
        if (match.Success)
        {
            var day = int.Parse(match.Groups[1].Value);
            var month = ParseMonth(match.Groups[2].Value);

            if (month > 0)
            {
                var year = DateTime.Now.Year;
                
                // If month is in past, use next year
                if (month < DateTime.Now.Month)
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

    private static int ParseMonth(string month)
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
