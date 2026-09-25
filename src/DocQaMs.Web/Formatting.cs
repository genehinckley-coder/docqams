using System.Globalization;
using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Conversations;

namespace DocQaMs.Web;

/// <summary>How the UI writes amounts, counts, times and settings.</summary>
internal static class Formatting
{
    private const string ModelPrefix = "Claude ";

    /// <summary>A US-dollar amount to the cent, as the design shows it: <c>$2.16</c>.</summary>
    public static string Money(decimal amount) => "$" + amount.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>A range of amounts, <c>$2.08–$2.16</c>, or one amount when both ends round to the same cent.</summary>
    public static string Money(CostRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        string lowest = Money(range.Lowest);
        string highest = Money(range.Highest);
        return lowest == highest ? lowest : lowest + "–" + highest;
    }

    /// <summary>A count and its noun: "1 turn", "2 turns".</summary>
    public static string Count(int count, string noun) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? noun : noun + "s");

    /// <summary>A whole number with thousands separators: <c>199,745</c>.</summary>
    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A fraction as a whole percentage: <c>0.126</c> is <c>13%</c>.</summary>
    public static string Percent(double fraction) =>
        (fraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>How long an answer took: <c>58 s</c>, or <c>1 min 04 s</c> from a minute on.</summary>
    public static string Elapsed(TimeSpan elapsed) => elapsed < TimeSpan.FromMinutes(1)
        ? string.Create(CultureInfo.InvariantCulture, $"{elapsed.TotalSeconds:0} s")
        : string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalMinutes} min {elapsed.Seconds:00} s");

    /// <summary>A time of day in the viewer's zone, on a 24-hour clock: <c>14:27</c>.</summary>
    public static string Time(DateTimeOffset time, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return TimeZoneInfo.ConvertTime(time, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>A date and time in the viewer's zone: <c>2026-09-15 14:27</c>.</summary>
    public static string Timestamp(DateTimeOffset time, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return TimeZoneInfo.ConvertTime(time, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>A model's name without the family prefix: <c>Opus 5</c>.</summary>
    public static string ShortName(SelectableModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.DisplayName.StartsWith(ModelPrefix, StringComparison.Ordinal)
            ? model.DisplayName[ModelPrefix.Length..]
            : model.DisplayName;
    }

    /// <summary>A model and level in running text: <c>Opus 5, High</c>.</summary>
    public static string Settings(AnswerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return ShortName(settings.Model) + ", " + settings.Effort.ToDisplayName();
    }

    /// <summary>A model and level as a badge shows them: <c>OPUS 5 · HIGH</c>.</summary>
    public static string SettingsBadge(AnswerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return (ShortName(settings.Model) + " · " + settings.Effort.ToDisplayName()).ToUpperInvariant();
    }
}
