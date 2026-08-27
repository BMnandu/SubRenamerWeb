namespace SubRenamer.Web.Models;

public enum SyncTaskStatus
{
    Queued,
    Running,
    AwaitingCommit,
    CompletedWithErrors,
    Failed,
    Cancelled,
    TimedOut
}

public enum SyncTaskItemStatus
{
    Planned,
    Running,
    Succeeded,
    RejectedLowQuality,
    Failed,
    Cancelled,
    TimedOut
}

public enum SyncQualityStatus
{
    NotEvaluated,
    Accepted,
    Rejected
}

public record SyncExecutionOptionsDto(
    bool RejectLowQuality = true,
    double MinScore = 0,
    double MaxQualityOffsetSeconds = 30,
    double MaxFramerateDeviation = 0.1,
    double MaxSearchOffsetSeconds = 60,
    int? TimeoutSeconds = null,
    string? ReferenceStream = null,
    double? SplitPenalty = null
);

public record SyncTaskRequestDto(
    List<SyncPlanItemRequestDto> Items,
    SyncMode DefaultMode = SyncMode.VideoGlobal,
    SyncExecutionOptionsDto? Options = null
);

public record SyncTaskCreatedDto(string TaskId, SyncTaskStatus Status);

public record SyncTaskItemResultDto(
    string ItemId,
    string Key,
    SyncMode Mode,
    SyncTaskItemStatus Status,
    string Video,
    string Subtitle,
    string? ReferenceSubtitle,
    string? CandidateFileName,
    string? StagingOutput,
    string? TargetPath,
    double Progress,
    double? OffsetSeconds,
    double? FramerateScaleFactor,
    SyncQualityStatus Quality,
    List<string> QualityReasons,
    string? Error,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt
);

public record SyncTaskDto(
    string TaskId,
    SyncTaskStatus Status,
    int Total,
    int Completed,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    List<SyncTaskItemResultDto> Items,
    List<string> Logs
);
