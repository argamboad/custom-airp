using System.Globalization;

namespace Airp.Web;

/// <summary>How long ago something happened, in the few words a list has room for.</summary>
/// <remarks>
/// Relative rather than a clock time: the server keeps UTC and the reader is somewhere else, and
/// "2h ago" is true in every time zone.
/// </remarks>
public static class Ages
{
    /// <summary>Describes a moment relative to now.</summary>
    /// <param name="at">The moment, or null when unknown.</param>
    /// <param name="now">Now; the caller's, so this can be tested.</param>
    /// <returns>A short label such as <c>3h ago</c>.</returns>
    public static string Describe(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } when)
        {
            return string.Empty;
        }

        var span = now - when;

        return span switch
        {
            { TotalMinutes: < 1 } => "just now",
            { TotalMinutes: < 60 } => string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}m ago"),
            { TotalHours: < 24 } => string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}h ago"),
            { TotalDays: < 30 } => string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalDays}d ago"),
            _ => when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
    }
}
