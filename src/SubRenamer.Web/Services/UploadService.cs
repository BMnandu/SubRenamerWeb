using SubRenamer.Web.Models;

namespace SubRenamer.Web.Services;

/// <summary>
/// 字幕上传管理:按会话隔离存储到 UploadDir,改名成功后由 RenameService 清理。
/// </summary>
public class UploadService(AppPaths paths)
{
    public async Task<UploadResultDto> SaveAsync(IFormFile file, string sessionId)
    {
        // 用 GetFullPath 规范化后再取文件名,防止 ../ 路径穿越
        var safeName = Path.GetFileName(Path.GetFullPath(file.FileName));
        if (string.IsNullOrEmpty(safeName) || safeName.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("非法文件名");

        var sessionDir = Path.Combine(paths.UploadDir, sessionId);
        Directory.CreateDirectory(sessionDir);

        var fullPath = Path.Combine(sessionDir, safeName);
        if (!fullPath.StartsWith(paths.UploadDir, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("路径越界");

        await using var fs = File.Create(fullPath);
        await file.CopyToAsync(fs);

        return new UploadResultDto(fullPath, safeName);
    }

    /// <summary>清理某会话的临时文件(改名后调用)</summary>
    public void CleanSession(string sessionId)
    {
        var sessionDir = Path.Combine(paths.UploadDir, sessionId);
        if (Directory.Exists(sessionDir) && sessionDir.StartsWith(paths.UploadDir, StringComparison.Ordinal))
            Directory.Delete(sessionDir, recursive: true);
    }
}