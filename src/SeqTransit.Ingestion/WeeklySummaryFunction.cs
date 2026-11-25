using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Lambda.Core;
using Npgsql;
using SeqTransit.Core;
using SeqTransit.Core.Reliability;
using SeqTransit.Core.Summaries;
using SeqTransit.Data;

namespace SeqTransit.Ingestion;

public sealed record WeeklySummaryRunResult(
    DateOnly WeekStart,
    string ModelId,
    bool Saved,
    string? SummaryText,
    IReadOnlyList<string> RejectedNumbers);

/// <summary>
/// Runs Mondays at 7 am Brisbane time: computes last week's reliability figures in SQL,
/// asks Amazon Bedrock (Converse API) to write a short plain-English summary using ONLY
/// those numbers, and rejects the reply if the guardrail catches any invented number.
/// </summary>
public sealed class WeeklySummaryFunction
{
    private static readonly Lazy<NpgsqlDataSource> DataSource =
        new(() => TransitDataSource.Create(DatabaseOptions.FromEnvironment()));

    private static readonly Lazy<AmazonBedrockRuntimeClient> Bedrock =
        new(() => new AmazonBedrockRuntimeClient());

    private readonly WeeklySummaryStore _store;
    private readonly string _modelId;
    private readonly OnTimePolicy _policy;

    public WeeklySummaryFunction()
    {
        _store = new WeeklySummaryStore(DataSource.Value);
        // The CDK stack is the single source of the model id (and of the IAM permission for it).
        _modelId = Settings.Required("BEDROCK_MODEL_ID");
        _policy = OnTimePolicy.FromEnvironment();
    }

    public async Task<WeeklySummaryRunResult> HandleAsync(Stream input, ILambdaContext context)
    {
        using var timeout = new CancellationTokenSource(Settings.Budget(context, TimeSpan.FromSeconds(5)));
        var now = TimeProvider.System.GetUtcNow();
        var today = BrisbaneTime.Today(TimeProvider.System);

        // Monday of the current week in Brisbane time.
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var currentWeekStart = today.AddDays(-daysSinceMonday);
        var lastWeekStart = currentWeekStart.AddDays(-7);

        var figures = await _store.ComputeWeeklyFiguresAsync(lastWeekStart, _policy, now, timeout.Token);
        if (figures.TotalObservedStops == 0)
        {
            // During the first week after deployment, fall back to the current week-to-date so the summary can still run.
            figures = await _store.ComputeWeeklyFiguresAsync(currentWeekStart, _policy, now, timeout.Token);
        }

        if (figures.TotalObservedStops == 0)
        {
            context.Logger.LogWarning($"WEEKLY_SUMMARY_SKIPPED weekStart={figures.WeekStart:yyyy-MM-dd} reason=no_observations");
            return new WeeklySummaryRunResult(figures.WeekStart, _modelId, false, null, []);
        }

        var request = new ConverseRequest
        {
            ModelId = _modelId,
            System = [new SystemContentBlock { Text = WeeklySummaryPrompt.SystemInstruction }],
            Messages =
            [
                new Message
                {
                    Role = ConversationRole.User,
                    Content = [new ContentBlock { Text = WeeklySummaryPrompt.BuildUserPrompt(figures) }],
                },
            ],
            InferenceConfig = new InferenceConfiguration
            {
                MaxTokens = 220,
                Temperature = 0.2f,
            },
        };

        var response = await Bedrock.Value.ConverseAsync(request, timeout.Token);
        var reply = response.Output?.Message?.Content?
            .Select(c => c.Text)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
            ?.Trim();

        var check = SummaryGuardrail.Validate(reply, figures);
        if (!check.IsValid)
        {
            var unmatchedList = string.Join(", ", check.UnmatchedNumbers);
            context.Logger.LogWarning(
                $"WEEKLY_SUMMARY_REJECTED weekStart={figures.WeekStart:yyyy-MM-dd} modelId={_modelId} unmatched=[{unmatchedList}]");
            return new WeeklySummaryRunResult(figures.WeekStart, _modelId, false, reply, check.UnmatchedNumbers);
        }

        await _store.SaveAsync(figures.WeekStart, reply!, figures, _modelId, now, timeout.Token);
        context.Logger.LogInformation(
            $"WEEKLY_SUMMARY_SAVED weekStart={figures.WeekStart:yyyy-MM-dd} modelId={_modelId} observedStops={figures.TotalObservedStops}");

        return new WeeklySummaryRunResult(figures.WeekStart, _modelId, true, reply, []);
    }
}
