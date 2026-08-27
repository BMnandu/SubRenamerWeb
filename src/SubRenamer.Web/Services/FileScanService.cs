using SubRenamer.Web.Models;

namespace SubRenamer.Web.Services;

/// <summary>应用路径配置</summary>
public record AppPaths(string MediaDir, string UploadDir, string? WorkDir = null)
{
    public string EffectiveWorkDir => WorkDir ?? Path.Combine(UploadDir, "work");
}

/// <summary>
/// 挂载目录扫描服务:递归扫描视频与字幕文件。
/// </summary>
public class FileScanService(SafePathService safePaths)
{
    /// <summary>扫描指定子目录(相对 MediaDir),不传则扫描整个 MediaDir</summary>
    public ScanResultDto Scan(string? subdir)
    {
        var root = safePaths.ResolveMediaSubdirectory(subdir);

        var videos = new List<FileEntryDto>();
        var subtitles = new List<FileEntryDto>();

        if (!Directory.Exists(root))
            return new ScanResultDto(videos, subtitles);

        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var discoveredPath in Directory.EnumerateFiles(root, "*", enumerationOptions))
        {
            if (!safePaths.TryEnsureMediaPath(discoveredPath, out var f))
                continue;

            var filename = Path.GetFileName(f);
            if (FileConstants.IsVideo(f))
                videos.Add(new FileEntryDto(f, filename, "video"));
            else if (FileConstants.IsSubtitle(f))
                subtitles.Add(new FileEntryDto(f, filename, "subtitle"));
        }

        return new ScanResultDto(videos, subtitles);
    }

    /// <summary>列出指定子目录下的一级目录(相对 MediaDir),用于前端目录浏览</summary>
    public List<DirInfoDto> ListDirectories(string? subdir)
    {
        var root = safePaths.ResolveMediaSubdirectory(subdir);

        if (!Directory.Exists(root)) return new List<DirInfoDto>();

        return Directory.GetDirectories(root)
            .Select(d => safePaths.TryEnsureMediaPath(d, out var safeDirectory) ? safeDirectory : null)
            .Where(d => d is not null)
            .Select(d => new DirInfoDto(
                Path.GetFileName(d!),
                Path.GetRelativePath(safePaths.MediaRoot, d!).Replace('\\', '/'),
                Directory.GetLastWriteTime(d!)))
            .OrderBy(d => d.Name)
            .ToList();
    }
}
