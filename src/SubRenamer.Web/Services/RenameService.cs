using SubRenamer.Web.Models;

namespace SubRenamer.Web.Services;

/// <summary>
/// 改名执行服务。支持两种模式:
/// 1. 混合模式:字幕来自 UploadDir,需拷贝到视频所在目录
/// 2. 同文件夹模式:字幕与视频同在 MediaDir,直接改名(可备份)
/// 自动按字幕路径所在目录判断模式。
/// </summary>
public class RenameService(AppPaths paths)
{
    public RenameResponseDto Rename(RenameRequestDto req)
    {
        var success = 0;
        var failed = 0;
        var details = new List<RenameResultItemDto>();

        // 检测一对多(同集数多字幕):自动提取语言后缀避免冲突,兼容 Emby 语言识别
        var hasDuplicateKey = req.Items
            .Where(i => !string.IsNullOrEmpty(i.Video) && !string.IsNullOrEmpty(i.Subtitle))
            .GroupBy(i => i.Key)
            .Any(g => g.Count() > 1);

        foreach (var item in req.Items)
        {
            var video = item.Video;
            var subtitle = item.Subtitle;
            try
            {
                if (string.IsNullOrEmpty(video) || string.IsNullOrEmpty(subtitle))
                    continue; // 未匹配项,跳过

                var videoPath = Path.GetFullPath(video);
                var subPath = Path.GetFullPath(subtitle);

                if (!IsPathSafe(videoPath) || !IsPathSafe(subPath))
                    throw new UnauthorizedAccessException("路径越界,禁止访问挂载/上传目录之外");

                var videoDir = Path.GetDirectoryName(videoPath) ?? "";
                var newName = LanguageHelper.ComputeNewName(videoPath, subPath, hasDuplicateKey, req.LangSuffix);
                var newPath = Path.Combine(videoDir, newName);

                var subDir = Path.GetDirectoryName(subPath) ?? "";
                var sameFolder = string.Equals(subDir, videoDir, StringComparison.OrdinalIgnoreCase);

                if (File.Exists(newPath))
                {
                    if (!req.Backup)
                        throw new IOException($"目标已存在:{newName}(启用备份以覆盖)");
                    BackupExisting(newPath, videoDir);
                }

                if (sameFolder)
                {
                    // 同文件夹:直接改名
                    File.Move(subPath, newPath, overwrite: true);
                }
                else
                {
                    // 混合模式:从上传目录拷贝到视频目录
                    File.Copy(subPath, newPath, overwrite: true);
                    // 改名成功后删除上传临时文件
                    TryDelete(subPath);
                }

                details.Add(new RenameResultItemDto(video, subtitle, newPath, true, null));
                success++;
            }
            catch (Exception ex)
            {
                details.Add(new RenameResultItemDto(video, subtitle, null, false, ex.Message));
                failed++;
            }
        }

        return new RenameResponseDto(success, failed, details);
    }

    private void BackupExisting(string filePath, string videoDir)
    {
        var backupDir = Path.Combine(videoDir, "SubBackup");
        Directory.CreateDirectory(backupDir);
        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var backupName = $"{Path.GetFileName(filePath)}.{stamp}.bak";
        File.Move(filePath, Path.Combine(backupDir, backupName));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* 忽略临时文件清理失败 */ }
    }

    private bool IsPathSafe(string path) =>
        path.StartsWith(paths.MediaDir, StringComparison.Ordinal)
        || path.StartsWith(paths.UploadDir, StringComparison.Ordinal);
}