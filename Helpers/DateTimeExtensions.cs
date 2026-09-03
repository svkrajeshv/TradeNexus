namespace NexusApp.Helpers;

public static class DateTimeExtensions
{
    private static readonly TimeZoneInfo IstZone = ResolveIstZone();

    private static TimeZoneInfo ResolveIstZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
            }
            catch (TimeZoneNotFoundException)
            {
                // Fallback to custom +5:30 offset
                return TimeZoneInfo.CreateCustomTimeZone(
                    "IST", 
                    TimeSpan.FromHours(5) + TimeSpan.FromMinutes(30), 
                    "India Standard Time", 
                    "India Standard Time"
                );
            }
        }
    }

    /// <summary>
    /// Converts a UTC DateTime to Indian Standard Time (IST).
    /// </summary>
    public static DateTime ToIst(this DateTime utcDateTime)
    {
        var utc = utcDateTime.Kind == DateTimeKind.Utc ? utcDateTime : DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, IstZone);
    }

    /// <summary>
    /// Converts a nullable UTC DateTime to Indian Standard Time (IST), or returns null.
    /// </summary>
    public static DateTime? ToIst(this DateTime? utcDateTime)
    {
        if (utcDateTime == null) return null;
        return utcDateTime.Value.ToIst();
    }

    /// <summary>
    /// Formats the DateTime as a string in IST format.
    /// </summary>
    public static string ToIstString(this DateTime utcDateTime, string format = "yyyy-MM-dd hh:mm:ss tt")
    {
        return utcDateTime.ToIst().ToString(format);
    }

    /// <summary>
    /// Formats the nullable DateTime as a string in IST format.
    /// </summary>
    public static string ToIstString(this DateTime? utcDateTime, string format = "yyyy-MM-dd hh:mm:ss tt")
    {
        if (utcDateTime == null) return "-";
        return utcDateTime.Value.ToIstString(format);
    }

    /// <summary>
    /// The current instant expressed as Indian Standard Time (IST) wall-clock time.
    /// Use this instead of <c>DateTime.Now</c> so behaviour is identical on non-IST hosts.
    /// </summary>
    public static DateTime IstNow() => DateTime.UtcNow.ToIst();

    /// <summary>
    /// The current IST calendar date (the trading day). Use this instead of
    /// <c>DateTime.Today</c>, which returns the host machine's date.
    /// </summary>
    public static DateTime IstToday() => IstNow().Date;

    /// <summary>
    /// Converts a DateTime expressed in Indian Standard Time (IST) to UTC.
    /// The input Kind is ignored and the value is treated as IST wall-clock time.
    /// </summary>
    public static DateTime IstToUtc(this DateTime istDateTime)
    {
        var unspecified = DateTime.SpecifyKind(istDateTime, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, IstZone);
    }

    /// <summary>
    /// Ensures the DateTime is specified as UTC. If Kind is Unspecified, treats it as UTC.
    /// </summary>
    public static DateTime EnsureUtc(this DateTime dt)
    {
        if (dt.Kind == DateTimeKind.Utc)
            return dt;

        if (dt.Kind == DateTimeKind.Local)
            return dt.ToUniversalTime();

        return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }

    /// <summary>
    /// Ensures the nullable DateTime is specified as UTC, or returns null.
    /// </summary>
    public static DateTime? EnsureUtc(this DateTime? dt)
    {
        if (dt == null) return null;
        return dt.Value.EnsureUtc();
    }
}
