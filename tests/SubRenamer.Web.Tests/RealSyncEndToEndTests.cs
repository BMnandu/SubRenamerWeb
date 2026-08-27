using System.Diagnostics;
using SubRenamer.Web.Models;
using SubRenamer.Web.Services;
using Xunit;

namespace SubRenamer.Web.Tests;

public sealed class RealSyncEndToEndTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        $"subrenamer-real-sync-{Guid.NewGuid():N}");

    [Fact]
    [Trait("Category", "EndToEnd")]
    public async Task WhenEnabled_VideoGlobal_RealFfmpegAndFfsubsync_CompletesCommitAndRollback()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("RUN_REAL_SYNC_E2E"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var python = Environment.GetEnvironmentVariable("PYTHON_EXECUTABLE") ?? "python3";
        var ffmpeg = Environment.GetEnvironmentVariable("FFMPEG_EXECUTABLE") ?? "ffmpeg";
        await RunProcessAsync(
            python,
            ["-c", "import ffsubsync; print(ffsubsync.__file__)"],
            TimeSpan.FromSeconds(30));

        var mediaRoot = Path.Combine(_testRoot, "media");
        var uploadRoot = Path.Combine(_testRoot, "uploads");
        var workRoot = Path.Combine(_testRoot, "work");
        Directory.CreateDirectory(mediaRoot);
        Directory.CreateDirectory(uploadRoot);
        Directory.CreateDirectory(workRoot);

        var video = Path.Combine(mediaRoot, "Show S01E01.mkv");
        await CreateSyntheticVideoAsync(ffmpeg, video);
        var subtitle = Path.Combine(uploadRoot, "Subtitle.01.zh.srt");
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "sync", "offset-plus-two.srt"),
            subtitle);

        var safePaths = new SafePathService(new AppPaths(mediaRoot, uploadRoot, workRoot));
        var planService = new SyncPlanService(safePaths, new SubtitleNamingService());
        var runtimeOptions = new SyncRuntimeOptions(1, 2, 60, 1, 100, python);
        await using var service = new SubSyncService(
            safePaths,
            planService,
            new FfsubsyncProcessRunner(runtimeOptions),
            runtimeOptions);

        var created = service.CreateTask(new SyncTaskRequestDto(
            [new SyncPlanItemRequestDto("01", video, subtitle)],
            Options: new SyncExecutionOptionsDto(
                RejectLowQuality: true,
                MinScore: 0,
                MaxQualityOffsetSeconds: 5,
                MaxFramerateDeviation: 0.1,
                MaxSearchOffsetSeconds: 5,
                TimeoutSeconds: 60)));
        var task = await WaitForTerminalAsync(service, created.TaskId, TimeSpan.FromSeconds(60));

        Assert.True(
            task.Status == SyncTaskStatus.AwaitingCommit,
            $"任务状态为 {task.Status}。错误：{string.Join(" | ", task.Items.Select(value => value.Error))}。日志：{string.Join(" | ", task.Logs)}");
        var item = Assert.Single(task.Items);
        Assert.Equal(SyncTaskItemStatus.Succeeded, item.Status);
        Assert.Equal(SyncQualityStatus.Accepted, item.Quality);
        Assert.InRange(item.OffsetSeconds!.Value, -2.15, -1.80);
        Assert.InRange(item.FramerateScaleFactor!.Value, 0.99, 1.01);
        Assert.True(File.Exists(item.StagingOutput));
        Assert.False(File.Exists(item.TargetPath));
        Assert.Contains(
            "00:00:01,0",
            await File.ReadAllTextAsync(item.StagingOutput!));

        var committed = await service.CommitTaskAsync(
            created.TaskId,
            new SyncCommitRequestDto(),
            CancellationToken.None);
        Assert.Equal(SyncFileOperationStatus.Committed, Assert.Single(committed!.Items).Status);
        Assert.True(File.Exists(item.TargetPath));

        var rolledBack = await service.RollbackTaskAsync(created.TaskId, CancellationToken.None);
        Assert.Equal(SyncFileOperationStatus.RolledBack, Assert.Single(rolledBack!.Items).Status);
        Assert.False(File.Exists(item.TargetPath));
        Assert.True(File.Exists(item.StagingOutput));
    }

    private static Task CreateSyntheticVideoAsync(string ffmpeg, string output) =>
        RunProcessAsync(
            ffmpeg,
            [
                "-hide_banner",
                "-loglevel",
                "error",
                "-y",
                "-f",
                "lavfi",
                "-i",
                "color=c=black:s=160x90:r=1:d=8",
                "-f",
                "lavfi",
                "-i",
                "anoisesrc=color=white:amplitude=0.5:r=16000:d=8",
                "-filter:a",
                "volume='if(between(t,1,2)+between(t,4,5),1,0)':eval=frame",
                "-map",
                "0:v:0",
                "-map",
                "1:a:0",
                "-c:v",
                "ffv1",
                "-c:a",
                "pcm_s16le",
                "-shortest",
                output
            ],
            TimeSpan.FromSeconds(30));

    private static async Task RunProcessAsync(
        string executable,
        IEnumerable<string> arguments,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"无法启动进程：{executable}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException($"进程执行超时：{executable}");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"进程执行失败：{executable}，退出码 {process.ExitCode}\n{stdout}\n{stderr}");
        }
    }

    private static async Task<SyncTaskDto> WaitForTerminalAsync(
        SubSyncService service,
        string taskId,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (true)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            var task = service.GetTask(taskId) ?? throw new InvalidOperationException("任务不存在");
            if (task.FinishedAt is not null)
                return task;
            await Task.Delay(50, cancellation.Token);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }
}
