using SeqTransit.Core.Summaries;

namespace SeqTransit.Core.Tests;

public sealed class SummaryGuardrailTests
{
    private static readonly WeeklyFigures SampleFigures = new(
        WeekStart: new DateOnly(2026, 11, 16),
        WeekEnd: new DateOnly(2026, 11, 22),
        TotalObservedStops: 46109,
        TotalRoutes: 416,
        OnTimePercent: 84.2,
        PreviousWeekOnTimePercent: 86.0,
        ChangePercentagePoints: -1.8,
        BestRoute: new WeeklyRouteHighlight("196-11", "196", "New Farm - Fairfield", 820, 96.5, 0.4),
        WorstRoute: new WeeklyRouteHighlight("574-7", "574", "Loganholme - Springwood", 340, 61.2, 4.7),
        WorstHour: new WeeklyHourHighlight(17, "5 pm", 5210, 72.3, 3.1),
        TotalCancellations: 12);

    [Fact]
    public void Accepts_reply_using_only_numbers_from_input_figures()
    {
        const string reply =
            "Between 16 and 22 November 2026, 84.2% of 46,109 observed stops across 416 routes ran on time, " +
            "down 1.8 percentage points from 86.0% the previous week, with 12 cancelled trips. " +
            "Route 196 led at 96.5% on time while Route 574 lagged at 61.2% with an average delay of 4.7 minutes. " +
            "The 5 pm hour was the slowest period at 72.3% on time.";

        var result = SummaryGuardrail.Validate(reply, SampleFigures);

        Assert.True(result.IsValid);
        Assert.Empty(result.UnmatchedNumbers);
    }

    [Fact]
    public void Rejects_reply_with_invented_percentage()
    {
        const string reply =
            "Overall punctuality was 84.2% across 416 routes, and peak trains achieved 91.7% on time.";

        var result = SummaryGuardrail.Validate(reply, SampleFigures);

        Assert.False(result.IsValid);
        Assert.Equal(["91.7"], result.UnmatchedNumbers);
    }

    [Fact]
    public void Rejects_reply_with_invented_route_or_derived_arithmetic()
    {
        const string reply =
            "Route 999 dropped to 50.0% on time, which is 35.3 percentage points below Route 196.";

        var result = SummaryGuardrail.Validate(reply, SampleFigures);

        Assert.False(result.IsValid);
        Assert.Contains("999", result.UnmatchedNumbers);
        Assert.Contains("50.0", result.UnmatchedNumbers);
        Assert.Contains("35.3", result.UnmatchedNumbers);
    }

    [Fact]
    public void Rejects_empty_reply()
    {
        var result = SummaryGuardrail.Validate("   ", SampleFigures);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.UnmatchedNumbers);
    }
}
