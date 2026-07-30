namespace SubRenamer.Web.Models;

/// <summary>扫描到的文件条目</summary>
public record FileEntryDto(string Path, string Filename, string Type);

/// <summary>目录扫描结果</summary>
public record ScanResultDto(List<FileEntryDto> Videos, List<FileEntryDto> Subtitles);

/// <summary>匹配请求:传文件名列表(Matcher 只看文件名)</summary>
public record MatchRequestDto(
    List<string> Videos,
    List<string> Subtitles,
    string? VideoRegex,
    string? SubtitleRegex
);

/// <summary>匹配结果项(Preview 为改名后的预览文件名)</summary>
public record MatchItemDto(string Key, string Video, string Subtitle, string? Preview = null);

/// <summary>匹配响应</summary>
public record MatchResponseDto(List<MatchItemDto> Items);

/// <summary>改名请求:传完整路径的 MatchItem 列表</summary>
public record RenameRequestDto(
    List<MatchItemDto> Items,
    bool Backup,
    string? LangSuffix
);

/// <summary>单个改名结果</summary>
public record RenameResultItemDto(string Video, string Subtitle, string? NewPath, bool Success, string? Error);

/// <summary>改名响应</summary>
public record RenameResponseDto(int Success, int Failed, List<RenameResultItemDto> Details);

/// <summary>字幕上传响应</summary>
public record UploadResultDto(string Path, string Filename);

/// <summary>统一错误响应</summary>
public record ErrorResponseDto(string Error);

/// <summary>目录条目信息(用于目录浏览,支持按日期排序)</summary>
public record DirInfoDto(string Name, string Path, DateTime LastModified);

/// <summary>调轴任务状态</summary>
public class SyncTask
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = "queued"; // queued/running/completed
    public int Total { get; set; }
    public int Done { get; set; }
    public double Progress { get; set; } // 当前项进度 0-1
    public string CurrentVideo { get; set; } = "";
    public List<MatchItemDto> Items { get; set; } = new();
    public List<string> Logs { get; set; } = new();
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

/// <summary>调轴请求</summary>
public record SyncRequestDto(List<MatchItemDto> Items);
