using SubRenamer.Web.Models;

namespace SubRenamer.Web.Services;

public sealed class SyncPlanService(SafePathService safePaths, SubtitleNamingService namingService)
{
    public SyncPlanResponseDto CreatePlan(SyncPlanRequestDto request)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var preparedItems = request.Items
            .Select((item, index) => PrepareItem(item, index, request.DefaultMode))
            .ToList();

        foreach (var group in preparedItems
                     .Where(item => item.Error is null)
                     .GroupBy(item => item.Video, PathComparer))
        {
            ApplyNames(group.ToList());
        }

        var responseItems = preparedItems.Select(item => new SyncPlanItemDto(
            item.ItemId,
            item.Key,
            item.Mode,
            item.Error is null ? SyncPlanItemStatus.Planned : SyncPlanItemStatus.Invalid,
            item.Video,
            item.Subtitle,
            item.ReferenceSubtitle,
            item.Language,
            item.CandidateFileName,
            item.TargetPath,
            item.TargetPath is not null && File.Exists(item.TargetPath),
            item.Error)).ToList();

        return new SyncPlanResponseDto(
            Guid.NewGuid().ToString("N"),
            createdAt,
            responseItems.Count(item => item.Status == SyncPlanItemStatus.Planned),
            responseItems.Count(item => item.Status == SyncPlanItemStatus.Invalid),
            responseItems);
    }

    private PreparedPlanItem PrepareItem(SyncPlanItemRequestDto item, int index, SyncMode defaultMode)
    {
        var itemId = string.IsNullOrWhiteSpace(item.ItemId) ? $"item-{index + 1}" : item.ItemId.Trim();
        var mode = item.Mode ?? defaultMode;

        try
        {
            var video = safePaths.EnsureMediaPath(item.Video);
            var subtitle = safePaths.EnsureInputPath(item.Subtitle);
            var reference = string.IsNullOrWhiteSpace(item.ReferenceSubtitle)
                ? null
                : safePaths.EnsureInputPath(item.ReferenceSubtitle);

            if (!File.Exists(video) || !FileConstants.IsVideo(video))
                throw new ArgumentException("视频文件不存在或格式不受支持");
            if (!File.Exists(subtitle) || !FileConstants.IsSubtitle(subtitle))
                throw new ArgumentException("字幕文件不存在或格式不受支持");
            if (reference is not null && (!File.Exists(reference) || !FileConstants.IsSubtitle(reference)))
                throw new ArgumentException("参考字幕不存在或格式不受支持");

            if (mode == SyncMode.SubtitleReference && reference is null)
                throw new ArgumentException("字幕参考模式必须提供 referenceSubtitle");

            namingService.ValidateInput(new SubtitleNameInput(video, subtitle, item.Language));

            return new PreparedPlanItem(itemId, item.Key, mode, video, subtitle, reference, item.Language);
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or IOException)
        {
            return new PreparedPlanItem(
                itemId,
                item.Key,
                mode,
                item.Video,
                item.Subtitle,
                item.ReferenceSubtitle,
                item.Language,
                error: ex.Message);
        }
    }

    private void ApplyNames(List<PreparedPlanItem> items)
    {
        try
        {
            var names = namingService.CreateUniqueNames(items
                .Select(item => new SubtitleNameInput(item.Video, item.Subtitle, item.Language))
                .ToList());

            for (var index = 0; index < items.Count; index++)
            {
                var videoDirectory = Path.GetDirectoryName(items[index].Video)
                    ?? throw new ArgumentException("无法确定视频目录");
                var targetPath = safePaths.EnsureMediaPath(Path.Combine(videoDirectory, names[index].FileName));
                items[index].Language = names[index].Language;
                items[index].CandidateFileName = names[index].FileName;
                items[index].TargetPath = targetPath;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or IOException)
        {
            for (var index = 0; index < items.Count; index++)
                items[index].Error = ex.Message;
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class PreparedPlanItem(
        string itemId,
        string key,
        SyncMode mode,
        string video,
        string subtitle,
        string? referenceSubtitle,
        string? language,
        string? candidateFileName = null,
        string? targetPath = null,
        string? error = null)
    {
        public string ItemId { get; } = itemId;
        public string Key { get; } = key;
        public SyncMode Mode { get; } = mode;
        public string Video { get; } = video;
        public string Subtitle { get; } = subtitle;
        public string? ReferenceSubtitle { get; } = referenceSubtitle;
        public string? Language { get; set; } = language;
        public string? CandidateFileName { get; set; } = candidateFileName;
        public string? TargetPath { get; set; } = targetPath;
        public string? Error { get; set; } = error;
    }
}
