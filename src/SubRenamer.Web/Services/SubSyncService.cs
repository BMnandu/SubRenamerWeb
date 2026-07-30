using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using SubRenamer.Web.Models;

namespace SubRenamer.Web.Services;

/// <summary>
/// 字幕自动调轴服务:调用 ffsubsync(Python wrapper)对齐字幕时间轴。
/// 异步任务管理,支持进度查询。
/// </summary>
public class SubSyncService(AppPaths paths)
{
    private readonly ConcurrentDictionary<string, SyncTask> _tasks = new();
    private static readonly string WrapperPath = Path.Combine(AppContext.BaseDirectory, "scripts", "sync_wrapper.py");

    public string CreateTask(List<MatchItemDto> items)
    {
        var taskId = Guid.NewGuid().ToString("N")[..8];
        var task = new SyncTask
        {
            Id = taskId,
            Status = "queued",
            Total = items.Count,
            Items = items,
            CreatedAt = DateTime.Now
        };
        _tasks[taskId] = task;
        _ = Task.Run(() => RunTask(task));
        return taskId;
    }

    public SyncTask? GetTask(string taskId) =>
        _tasks.TryGetValue(taskId, out var t) ? t : null;

    private async Task RunTask(SyncTask task)
    {
        task.Status = "running";
        foreach (var item in task.Items)
        {
            if (string.IsNullOrEmpty(item.Video) || string.IsNullOrEmpty(item.Subtitle)) continue;
            try
            {
                var video = Path.GetFullPath(item.Video);
                var subtitle = Path.GetFullPath(item.Subtitle);
                if (!IsPathSafe(video) || !IsPathSafe(subtitle))
                    throw new UnauthorizedAccessException("路径越界");

                var videoDir = Path.GetDirectoryName(video) ?? "";
                var videoNameNoExt = Path.GetFileNameWithoutExtension(video);
                var subExt = Path.GetExtension(subtitle);
                var output = Path.Combine(videoDir, $"{videoNameNoExt}{subExt}");

                task.CurrentVideo = Path.GetFileName(video);
                task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 开始调轴:{task.CurrentVideo}");

                await RunFfsubsync(task, video, subtitle, output);

                task.Done++;
                task.Progress = 0;
                task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 完成 → {output}");
            }
            catch (Exception ex)
            {
                task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 失败:{ex.Message}");
                task.Error = ex.Message;
                task.Done++; // 失败也算处理完一个,继续下一个
            }
        }
        task.Status = string.IsNullOrEmpty(task.Error) ? "completed" : "completed";
        task.FinishedAt = DateTime.Now;
        task.CurrentVideo = "";
        task.Progress = 1;
    }

    private async Task RunFfsubsync(SyncTask task, string video, string subtitle, string output)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python3",
            Arguments = $"\"{WrapperPath}\" \"{video}\" \"{subtitle}\" \"{output}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        while (!proc.StandardOutput.EndOfStream)
        {
            var line = await proc.StandardOutput.ReadLineAsync();
            if (string.IsNullOrEmpty(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var type = doc.RootElement.GetProperty("type").GetString();
                if (type == "progress" && doc.RootElement.TryGetProperty("fraction", out var f) && f.ValueKind == JsonValueKind.Number)
                    task.Progress = f.GetDouble();
                else if (type == "error")
                    throw new Exception(doc.RootElement.GetProperty("message").GetString() ?? "ffsubsync 错误");
            }
            catch (Exception ex) when (!ex.Message.Contains("ffsubsync"))
            {
                // 非 JSON 行或解析失败,忽略
            }
        }
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
            throw new Exception($"ffsubsync 退出码 {proc.ExitCode}");
    }

    private bool IsPathSafe(string path) =>
        path.StartsWith(paths.MediaDir, StringComparison.Ordinal)
        || path.StartsWith(paths.UploadDir, StringComparison.Ordinal);
}
