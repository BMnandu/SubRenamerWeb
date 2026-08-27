using SubRenamer.Web.Models;

namespace SubRenamer.Web.Services;

/// <summary>
/// 字幕上传管理:按会话隔离存储到 UploadDir,改名成功后由 RenameService 清理。
/// </summary>
public class UploadService(SafePathService safePaths)
{
    public async Task<UploadResultDto> SaveAsync(IFormFile file, string sessionId)
    {
        var safeName = file.FileName;
        var sessionDir = safePaths.ResolveUploadSessionDirectory(sessionId);
        Directory.CreateDirectory(sessionDir);

        var fullPath = safePaths.ResolveUploadFile(sessionId, safeName);

        await using var fs = File.Create(fullPath);
        await file.CopyToAsync(fs);

        return new UploadResultDto(fullPath, safeName);
    }

    /// <summary>清理某会话的临时文件(改名后调用)</summary>
    public void CleanSession(string sessionId)
    {
        var sessionDir = safePaths.ResolveUploadSessionDirectory(sessionId);
        if (Directory.Exists(sessionDir))
            Directory.Delete(sessionDir, recursive: true);
    }
}
