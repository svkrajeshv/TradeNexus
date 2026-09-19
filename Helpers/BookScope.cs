using NexusApp.Models;

namespace NexusApp.Helpers;

/// <summary>
/// Single definition of the "live (terminal) book vs paper book" split.
/// <para>
/// A position/order belongs to the paper book when its <see cref="TradingAccount.ClientId"/>
/// is <see cref="PaperClientId"/>; everything else is the live/terminal book. This mirrors
/// <see cref="TradingAccount.IsPaperAccount"/>, which is case-insensitive.
/// </para>
/// <para>
/// IMPORTANT - in EF Core queries always write
/// <c>EF.Functions.Like(x.TradingAccount.ClientId, BookScope.PaperClientId)</c>.
/// Two other spellings look reasonable but are both wrong here:
/// <list type="bullet">
/// <item><description>
/// <c>ClientId == "PAPER"</c> is case-sensitive under SQLite, so an account named "Paper"
/// is classified live on the server while the UI treats it as paper.
/// </description></item>
/// <item><description>
/// <c>ClientId.Equals("PAPER", StringComparison...)</c> throws at runtime - EF Core cannot
/// translate the <see cref="StringComparison"/> overloads. Note that a <c>ToUpper()</c>
/// comparison, while translatable, gets silently rewritten into exactly that broken form
/// by the CA1862 analyzer / "Use string.Equals" code cleanup. <c>EF.Functions.Like</c> is
/// translatable, case-insensitive, and immune to that rewrite.
/// </description></item>
/// </list>
/// </para>
/// </summary>
public static class BookScope
{
    /// <summary>The reserved <see cref="TradingAccount.ClientId"/> of the simulated book.</summary>
    public const string PaperClientId = "PAPER";

    /// <summary>In-memory paper-book test for an account.</summary>
    public static bool IsPaper(TradingAccount? account) =>
        string.Equals(account?.ClientId, PaperClientId, StringComparison.OrdinalIgnoreCase);

    /// <summary>In-memory paper-book test for a position, via its loaded account.</summary>
    public static bool IsPaper(Position position) => IsPaper(position.TradingAccount);

    /// <summary>In-memory live/terminal-book test for a position, via its loaded account.</summary>
    public static bool IsLive(Position position) => !IsPaper(position);
}
