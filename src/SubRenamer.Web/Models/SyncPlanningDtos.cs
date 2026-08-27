namespace SubRenamer.Web.Models;

public enum SyncMode
{
    SubtitleReference,
    VideoGlobal,
    VideoSplit,
    NoSync
}

public enum SyncPlanItemStatus
{
    Planned,
    Invalid
}

public record SyncPlanRequestDto(
    List<SyncPlanItemRequestDto> Items,
    SyncMode DefaultMode = SyncMode.VideoGlobal
);

public record SyncPlanItemRequestDto(
    string Key,
    string Video,
    string Subtitle,
    string? ItemId = null,
    string? Language = null,
    SyncMode? Mode = null,
    string? ReferenceSubtitle = null
);

public record SyncPlanItemDto(
    string ItemId,
    string Key,
    SyncMode Mode,
    SyncPlanItemStatus Status,
    string Video,
    string Subtitle,
    string? ReferenceSubtitle,
    string? Language,
    string? CandidateFileName,
    string? TargetPath,
    bool TargetExists,
    string? Error
);

public record SyncPlanResponseDto(
    string PlanId,
    DateTimeOffset CreatedAt,
    int Planned,
    int Invalid,
    List<SyncPlanItemDto> Items
);
