using System.Globalization;
using System.Text;

namespace Graft.Core;

/// <summary>
/// GitLab's duration vocabulary: <c>1mo 2w 3d 4h 5m</c>, read and written. A month is four weeks, a week
/// five days and a day eight hours, which are GitLab's own conversions and the ones a team's estimates are
/// usually already in.
/// </summary>
public static class TimeTracking
{
    private const long Minute = 60;
    private const long Hour = 60 * Minute;
    private const long Day = 8 * Hour;
    private const long Week = 5 * Day;
    private const long Month = 4 * Week;

    /// <summary>A year of eight-hour days; anything larger is a mistake rather than an estimate.</summary>
    public const long MaximumSeconds = 12 * Month;

    /// <summary>
    /// Parses a duration like <c>3h 30m</c>, <c>2d</c> or <c>1w 1d</c>. Also accepts a bare number of
    /// seconds for a client that stores one. Returns false for anything it does not understand, including
    /// an empty string, so a caller never mistakes "nothing" for "zero".
    /// </summary>
    public static bool TryParse(string? value, out long seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string text = value.Trim();
        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long raw))
        {
            seconds = raw;
            return raw <= MaximumSeconds;
        }

        int index = 0;
        bool any = false;
        while (index < text.Length)
        {
            while (index < text.Length && text[index] == ' ') index++;
            if (index >= text.Length) break;
            int start = index;
            while (index < text.Length && char.IsAsciiDigit(text[index])) index++;
            if (start == index) return false;
            long amount = long.Parse(text.AsSpan(start, index - start), NumberStyles.None, CultureInfo.InvariantCulture);
            int unitStart = index;
            while (index < text.Length && char.IsAsciiLetter(text[index])) index++;
            long unit = text.AsSpan(unitStart, index - unitStart) switch
            {
                "mo" => Month,
                "w" => Week,
                "d" => Day,
                "h" => Hour,
                "m" => Minute,
                "s" => 1,
                _ => 0
            };
            if (unit == 0) return false;
            seconds += amount * unit;
            if (seconds > MaximumSeconds) return false;
            any = true;
        }
        return any;
    }

    /// <summary>Writes seconds back in the same vocabulary, largest unit first. Null for zero, which is
    /// what GitLab reports for "nothing tracked".</summary>
    public static string? Humanize(long seconds)
    {
        if (seconds <= 0) return null;
        StringBuilder text = new();
        Append(text, ref seconds, Month, "mo");
        Append(text, ref seconds, Week, "w");
        Append(text, ref seconds, Day, "d");
        Append(text, ref seconds, Hour, "h");
        Append(text, ref seconds, Minute, "m");
        Append(text, ref seconds, 1, "s");
        return text.ToString();
    }

    private static void Append(StringBuilder text, ref long seconds, long unit, string suffix)
    {
        if (seconds < unit) return;
        if (text.Length > 0) text.Append(' ');
        text.Append((seconds / unit).ToString(CultureInfo.InvariantCulture)).Append(suffix);
        seconds %= unit;
    }
}
