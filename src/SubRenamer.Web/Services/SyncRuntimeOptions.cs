namespace SubRenamer.Web.Services;

public sealed record SyncRuntimeOptions(
    int MaxConcurrentSyncs,
    int MaxQueueSize,
    int TimeoutSeconds,
    int TaskRetentionHours,
    int MaxLogEntries,
    string PythonExecutable)
{
    public static SyncRuntimeOptions FromConfiguration(IConfiguration configuration) => new(
        ReadPositiveInt(configuration, "MAX_CONCURRENT_SYNCS", 1),
        ReadPositiveInt(configuration, "MAX_QUEUE_SIZE", 20),
        ReadPositiveInt(configuration, "SYNC_TIMEOUT_SECONDS", 900),
        ReadPositiveInt(configuration, "TASK_RETENTION_HOURS", 24),
        ReadPositiveInt(configuration, "MAX_TASK_LOG_ENTRIES", 200),
        configuration["PYTHON_EXECUTABLE"] ?? "python3");

    private static int ReadPositiveInt(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], out var value) && value > 0 ? value : fallback;
}
