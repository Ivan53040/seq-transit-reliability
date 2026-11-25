using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SeqTransit.Core.Summaries;

public sealed record WeeklyRouteHighlight(
    string RouteId,
    string RouteShortName,
    string RouteLongName,
    int ObservedStops,
    double OnTimePercent,
    double AverageDelayMinutes);

public sealed record WeeklyHourHighlight(
    int Hour,
    string Label,
    int ObservedStops,
    double OnTimePercent,
    double AverageDelayMinutes);

/// <summary>
/// Every number the weekly summary model is allowed to cite, computed in SQL beforehand.
/// The model only writes sentences about these figures; it never does arithmetic.
/// </summary>
public sealed record WeeklyFigures(
    DateOnly WeekStart,
    DateOnly WeekEnd,
    int TotalObservedStops,
    int TotalRoutes,
    double OnTimePercent,
    double? PreviousWeekOnTimePercent,
    double? ChangePercentagePoints,
    WeeklyRouteHighlight? BestRoute,
    WeeklyRouteHighlight? WorstRoute,
    WeeklyHourHighlight? WorstHour,
    int TotalCancellations);

public sealed record GuardrailResult(bool IsValid, IReadOnlyList<string> UnmatchedNumbers);

public static class WeeklySummaryPrompt
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string SerializeFigures(WeeklyFigures figures) =>
        JsonSerializer.Serialize(figures, JsonOptions);

    public static string SystemInstruction =>
        "You write a concise, plain-English weekly transit reliability summary for South East Queensland commuters. " +
        "Write 2 to 3 short sentences. " +
        "STRICT RULE: You must ONLY use numbers that appear verbatim in the JSON figures provided by the user. " +
        "Never calculate, round differently, estimate, or invent any new numbers, dates, rankings, or durations. " +
        "Do not use bullet points, headings, or markdown.";

    public static string BuildUserPrompt(WeeklyFigures figures) =>
        $"""
        Write a short plain-English summary of South East Queensland public transport reliability using ONLY the numbers in this JSON:

        {SerializeFigures(figures)}
        """;
}

/// <summary>
/// Rejects any model reply that contains a number not present in the SQL-computed input figures.
/// </summary>
public static partial class SummaryGuardrail
{
    // Matches comma-grouped numbers (46,109), plain integers (2026), and decimals (84.2, -1.8).
    [GeneratedRegex(@"(?<![A-Za-z0-9.])[-+−]?(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    public static GuardrailResult Validate(string? reply, WeeklyFigures figures)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return new GuardrailResult(false, ["<empty reply>"]);
        }

        var allowed = BuildAllowedNumbers(figures);
        var unmatched = new List<string>();

        foreach (Match match in NumberPattern().Matches(reply))
        {
            var raw = match.Value;
            var normalized = raw.Replace(",", "").Replace('−', '-');
            if (!decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            {
                continue;
            }

            // Allow exact decimal match or absolute value match for signed change figures (e.g. "-1.4" vs "1.4").
            if (!allowed.Contains(parsed) && !(parsed < 0 && allowed.Contains(Math.Abs(parsed))))
            {
                unmatched.Add(raw);
            }
        }

        return new GuardrailResult(unmatched.Count == 0, unmatched);
    }

    private static HashSet<decimal> BuildAllowedNumbers(WeeklyFigures f)
    {
        var set = new HashSet<decimal>();

        void AddInt(int v) => set.Add(v);
        void AddDouble(double v)
        {
            var d = Math.Round((decimal)v, 4);
            set.Add(d);
            // Also allow exact 1-decimal representation and absolute magnitude if negative.
            var oneDec = Math.Round(d, 1, MidpointRounding.AwayFromZero);
            set.Add(oneDec);
            if (d < 0)
            {
                set.Add(Math.Abs(d));
                set.Add(Math.Abs(oneDec));
            }
        }
        void AddDate(DateOnly date)
        {
            set.Add(date.Year);
            set.Add(date.Month);
            set.Add(date.Day);
        }
        void AddNumbersInText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (Match m in NumberPattern().Matches(text))
            {
                var norm = m.Value.Replace(",", "").Replace('−', '-');
                if (decimal.TryParse(norm, NumberStyles.Number, CultureInfo.InvariantCulture, out var v))
                {
                    set.Add(v);
                    if (v < 0) set.Add(Math.Abs(v));
                }
            }
        }

        AddDate(f.WeekStart);
        AddDate(f.WeekEnd);
        AddInt(f.TotalObservedStops);
        AddInt(f.TotalRoutes);
        AddDouble(f.OnTimePercent);
        AddInt(f.TotalCancellations);

        if (f.PreviousWeekOnTimePercent is { } prev)
        {
            AddDouble(prev);
        }
        if (f.ChangePercentagePoints is { } change)
        {
            AddDouble(change);
        }

        void AddRoute(WeeklyRouteHighlight? route)
        {
            if (route is null) return;
            AddNumbersInText(route.RouteShortName);
            AddNumbersInText(route.RouteLongName);
            AddNumbersInText(route.RouteId);
            AddInt(route.ObservedStops);
            AddDouble(route.OnTimePercent);
            AddDouble(route.AverageDelayMinutes);
        }

        AddRoute(f.BestRoute);
        AddRoute(f.WorstRoute);

        if (f.WorstHour is { } hour)
        {
            AddInt(hour.Hour);
            // Allow 12-hour clock equivalent (e.g. 17 -> 5 pm, 0 -> 12 am) and ":00" minute suffix.
            var hour12 = hour.Hour % 12 == 0 ? 12 : hour.Hour % 12;
            AddInt(hour12);
            AddInt(0);
            AddNumbersInText(hour.Label);
            AddInt(hour.ObservedStops);
            AddDouble(hour.OnTimePercent);
            AddDouble(hour.AverageDelayMinutes);
        }

        return set;
    }
}
