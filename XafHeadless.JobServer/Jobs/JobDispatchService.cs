namespace XafHeadless.JobServer.Jobs;

// Adapted from a companion headless implementation's job dispatch service, trimmed to this host's single job type.
// SyncScheduleByName/RemoveScheduleByName are kept for Phase 5 (cron sync); the by-type switches
// collapse to one case since there is exactly one possible JobDefinition row per type here.
public sealed class JobDispatchService(IJobDispatcher dispatcher, ILogger<JobDispatchService> logger) {
    public const string EmailOrdersReport = "EmailOrdersReport";
    public const string RecurringJobId = nameof(EmailOrdersReportCommand);
    public static IReadOnlyCollection<string> SupportedJobTypes { get; } = [EmailOrdersReport];

    public async Task DispatchByNameAsync(string jobTypeName, string? parametersJson, CancellationToken ct) {
        if (jobTypeName != EmailOrdersReport)
            throw new ArgumentException($"Unknown job type: {jobTypeName}", nameof(jobTypeName));
        logger.LogInformation("Dispatching job by name: {JobTypeName}", jobTypeName);
        var command = Deserialize(parametersJson);
        await dispatcher.DispatchAsync(command, ct);
    }

    public void SyncScheduleByName(string jobTypeName, string? parametersJson, string? cronExpression, bool isEnabled) {
        if (jobTypeName != EmailOrdersReport)
            throw new ArgumentException($"Unknown job type: {jobTypeName}", nameof(jobTypeName));
        if (!isEnabled || string.IsNullOrWhiteSpace(cronExpression)) { RemoveScheduleByName(jobTypeName); return; }
        dispatcher.Schedule(Deserialize(parametersJson), cronExpression, RecurringJobId);
    }

    public void RemoveScheduleByName(string jobTypeName) => dispatcher.RemoveSchedule(RecurringJobId);

    // M-4 (SVR-001 Dispatch F carry-forward): guard against a deserialized command whose EmailRecipients
    // is null/empty (e.g. payload "{}" or "null") -- with email now live, a missing recipient means
    // "send to nobody", which must fail loudly here rather than dispatch a job that silently no-ops.
    static EmailOrdersReportCommand Deserialize(string? json) {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("EmailRecipients is required.", nameof(json));
        var command = System.Text.Json.JsonSerializer.Deserialize<EmailOrdersReportCommand>(json)!;
        if (string.IsNullOrWhiteSpace(command.EmailRecipients))
            throw new ArgumentException("EmailRecipients is required.", nameof(json));
        return command;
    }
}
