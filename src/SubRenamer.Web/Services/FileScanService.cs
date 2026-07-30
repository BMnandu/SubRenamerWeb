using SubRenamer.Web.Models;

namespace SubRenamer.Web.Services;

/// <summary>应用路径配置</summary>
public record AppPaths(string MediaDir, string UploadDir);

/// <summary>
/// 挂载目录扫描服务:递归扫描视频与字幕文件。
/// </summary>
public class FileScanService(AppPaths paths)
{
    /// <summary>扫描指定子目录(相对 MediaDir),不传则扫描整个 MediaDir</summary>
    public ScanResultDto Scan(string? subdir)
    {
        var root = string.IsNullOrEmpty(subdir)
            ? paths.MediaDir
            : Path.GetFullPath(Path.Combine(paths.MediaDir, subdir.TrimStart('/')));

        if (!root.StartsWith(paths.MediaDir, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("路径越界,禁止访问 MediaDir 之外");

        var videos = new List<FileEntryDto>();
        var subtitles = new List<FileEntryDto>();

        if (!Directory.Exists(root))
            return new ScanResultDto(videos, subtitles);

        foreach (var f in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
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
        var root = string.IsNullOrEmpty(subdir)
            ? paths.MediaDir
            : Path.GetFullPath(Path.Combine(paths.MediaDir, subdir.TrimStart('/')));

        if (!root.StartsWith(paths.MediaDir, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("路径越界");

        if (!Directory.Exists(root)) return new List<DirInfoDto>();

        return Directory.GetDirectories(root)
            .Select(d => new DirInfoDto(
                Path.GetFileName(d),
                Path.GetRelativePath(paths.MediaDir, d).Replace('\\', '/'),
                Directory.GetLastWriteTime(d)))
            .OrderBy(d => d.Name)
            .ToList();
    }
}