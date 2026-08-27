using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using SubRenamer.Web.Models;
using SubRenamer.Web.Services;
using Xunit;

namespace SubRenamer.Web.Tests;

public sealed partial class RealSyncEndToEndTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _mediaRoot;
    private readonly string _uploadRoot;
    private readonly string _workRoot;

    public RealSyncEndToEndTests()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            $"subrenamer-real-sync-{Guid.NewGuid():N}");
        _mediaRoot = Path.Combine(_testRoot, "media");
        _uploadRoot = Path.Combine(_testRoot, "uploads");
        _workRoot = Path.Combine(_testRoot, "work");
        Directory.CreateDirectory(_mediaRoot);
        Directory.CreateDirectory(_uploadRoot);
        Directory.CreateDirectory(_workRoot);
    }

    [Fact]
    [Trait("Category", "EndToEnd")]
    public async Task WhenEnabled_VideoGlobal_RealFfmpegAndFfsubsync_CompletesCommitAndRollback()
    {
        if (!IsEnabled())
            return;

        var (python, ffmpeg) = await ResolveDependenciesAsync();
        var video = Path.Combine(_mediaRoot, "Show S01E01.mkv");
        await CreateSyntheticVideoAsync(ffmpeg, video, 8, [(1, 2), (4, 5)]);
        var subtitle = CopyFixture("offset-plus-two.srt", "Subtitle.01.zh.srt");
        await using var service = CreateService(python);

        var created = service.CreateTask(CreateRequest(video, subtitle, SyncMode.VideoGlobal));
        var task = await WaitForTerminalAsync(service, created.TaskId, TimeSpan.FromSeconds(60));
        var item = AssertSuccessfulTask(task, SyncMode.VideoGlobal, -2.15, -1.80);
        AssertCueStarts(item.StagingOutput!, [1, 4]);
        Assert.False(File.Exists(item.TargetPath));

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

    [Fact]
    [Trait("Category", "EndToEnd")]
    public async Task WhenEnabled_SubtitleReference_UsesReferenceTimelineWithoutChangingInputs()
    {
        if (!IsEnabled())
            return;

        var (python, ffmpeg) = await ResolveDependenciesAsync();
        var video = Path.Combine(_mediaRoot, "Show S01E01.mkv");
        await CreateSyntheticVideoAsync(ffmpeg, video, 8, [(1, 2), (4, 5)]);
        var subtitle = CopyFixture("offset-plus-two.srt", "Subtitle.01.zh.srt");
        var reference = CopyFixture("reference.srt", "Reference.01.srt");
        var originalSubtitle = await File.ReadAllTextAsync(subtitle);
        var originalReference = await File.ReadAllTextAsync(reference);
        await using var service = CreateService(python);

        var created = service.CreateTask(CreateRequest(
            video,
            subtitle,
            SyncMode.SubtitleReference,
            referenceSubtitle: reference));
        var task = await WaitForTerminalAsync(service, created.TaskId, TimeSpan.FromSeconds(60));
        var item = AssertSuccessfulTask(task, SyncMode.SubtitleReference, -2.15, -1.80);

        Assert.Equal(reference, item.ReferenceSubtitle);
        AssertCueStarts(item.StagingOutput!, [1, 4]);
        Assert.Equal(originalSubtitle, await File.ReadAllTextAsync(subtitle));
        Assert.Equal(originalReference, await File.ReadAllTextAsync(reference));
        Assert.False(File.Exists(item.TargetPath));
    }

    [Fact]
    [Trait("Category", "EndToEnd")]
    public async Task WhenEnabled_VideoSplit_CorrectsTwoDifferentOffsets()
    {
        if (!IsEnabled())
            return;

        var (python, ffmpeg) = await ResolveDependenciesAsync();
        var video = Path.Combine(_mediaRoot, "Show S01E01.mkv");
        await CreateSyntheticVideoAsync(
            ffmpeg,
            video,
            24,
            [(1, 2), (3, 4), (6, 7), (9, 10), (12, 13), (16, 17)]);
        var subtitle = CopyFixture("piecewise-offset.srt", "Subtitle.01.zh.srt");
        await using var service = CreateService(python);

        var created = service.CreateTask(CreateRequest(
            video,
            subtitle,
            SyncMode.VideoSplit,
            splitPenalty: 0.5,
            maxOffsetSeconds: 6));
        var task = await WaitForTerminalAsync(service, created.TaskId, TimeSpan.FromSeconds(60));
        var item = AssertSuccessfulTask(task, SyncMode.VideoSplit, -3.15, -2.80);

        AssertCueStarts(item.StagingOutput!, [1, 3, 6, 9, 12, 16]);
        Assert.Contains(task.Logs, line => line.Contains("split alignment", StringComparison.Ordinal));
        Assert.False(File.Exists(item.TargetPath));
    }

    private SubSyncService CreateService(string python)
    {
        var safePaths = new SafePathService(new AppPaths(_mediaRoot, _uploadRoot, _workRoot));
        var planService = new SyncPlanService(safePaths, new SubtitleNamingService());
        var runtimeOptions = new SyncRuntimeOptions(1, 2, 60, 1, 100, python);
        return new SubSyncService(
            safePaths,
            planService,
            new FfsubsyncProcessRunner(runtimeOptions),
            runtimeOptions);
    }

    private static SyncTaskRequestDto CreateRequest(
        string video,
        string subtitle,
        SyncMode mode,
        string? referenceSubtitle = null,
        double? splitPenalty = null,
        double maxOffsetSeconds = 5) =>
        new(
            [new SyncPlanItemRequestDto(
                "01",
                video,
                subtitle,
                Mode: mode,
                ReferenceSubtitle: referenceSubtitle)],
            Options: new SyncExecutionOptionsDto(
                RejectLowQuality: true,
                MinScore: 0,
                MaxQualityOffsetSeconds: maxOffsetSeconds,
                MaxFramerateDeviation: 0.1,
                MaxSearchOffsetSeconds: maxOffsetSeconds,
                TimeoutSeconds: 60,
                SplitPenalty: splitPenalty));

    private string CopyFixture(string fixtureName, string destinationName)
    {
        var destination = Path.Combine(_uploadRoot, destinationName);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "sync", fixtureName),
            destination);
        return destination;
    }

    private static SyncTaskItemResultDto AssertSuccessfulTask(
        SyncTaskDto task,
        SyncMode expectedMode,
        double minimumOffset,
        double maximumOffset)
    {
        Assert.True(
            task.Status == SyncTaskStatus.AwaitingCommit,
            $"任务状态为 {task.Status}。错误：{string.Join(" | ", task.Items.Select(value => value.Error))}。日志：{string.Join(" | ", task.Logs)}");
        var item = Assert.Single(task.Items);
        Assert.Equal(expectedMode, item.Mode);
        Assert.Equal(SyncTaskItemStatus.Succeeded, item.Status);
        Assert.Equal(SyncQualityStatus.Accepted, item.Quality);
        Assert.InRange(item.OffsetSeconds!.Value, minimumOffset, maximumOffset);
        Assert.InRange(item.FramerateScaleFactor!.Value, 0.99, 1.01);
        Assert.True(File.Exists(item.StagingOutput));
        return item;
    }

    private static void AssertCueStarts(string subtitlePath, IReadOnlyList<double> expectedStarts)
    {
        var content = File.ReadAllText(subtitlePath);
        var actualStarts = CueStartRegex()
            .Matches(content)
            .Select(match =>
                int.Parse(match.Groups["hours"].Value, CultureInfo.InvariantCulture) * 3600
                + int.Parse(match.Groups["minutes"].Value, CultureInfo.InvariantCulture) * 60
                + int.Parse(match.Groups["seconds"].Value, CultureInfo.InvariantCulture)
                + int.Parse(match.Groups["milliseconds"].Value, CultureInfo.InvariantCulture) / 1000d)
            .ToList();

        Assert.Equal(expectedStarts.Count, actualStarts.Count);
        for (var index = 0; index < expectedStarts.Count; index++)
        {
            Assert.InRange(
                actualStarts[index],
                expectedStarts[index] - 0.12,
                expectedStarts[index] + 0.12);
        }
    }

    private static bool IsEnabled() =>
        string.Equals(
            Environment.GetEnvironmentVariable("RUN_REAL_SYNC_E2E"),
            "1",
            StringComparison.Ordinal);

    private static async Task<(string Python, string Ffmpeg)> ResolveDependenciesAsync()
    {
        var python = Environment.GetEnvironmentVariable("PYTHON_EXECUTABLE") ?? "python3";
        var ffmpeg = Environment.GetEnvironmentVariable("FFMPEG_EXECUTABLE") ?? "ffmpeg";
        await RunProcessAsync(
            python,
            ["-c", "import ffsubsync; print(ffsubsync.__file__)"],
            TimeSpan.FromSeconds(30));
        return (python, ffmpeg);
    }

    private static Task CreateSyntheticVideoAsync(
        string ffmpeg,
        string output,
        int durationSeconds,
        IReadOnlyList<(int Start, int End)> speechIntervals)
    {
        var speechExpression = string.Join(
            "+",
            speechIntervals.Select(interval => $"between(t,{interval.Start},{interval.End})"));
        return RunProcessAsync(
            ffmpeg,
            [
                "-hide_banner",
                "-loglevel",
                "error",
                "-y",
                "-f",
                "lavfi",
                "-i",
                $"color=c=black:s=160x90:r=1:d={durationSeconds}",
                "-f",
                "lavfi",
                "-i",
                $"anoisesrc=color=white:amplitude=0.5:r=16000:d={durationSeconds}",
                "-filter:a",
                $"volume='if({speechExpression},1,0)':eval=frame",
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
    }

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

    [GeneratedRegex(
        @"(?m)^(?<hours>\d{2}):(?<minutes>\d{2}):(?<seconds>\d{2}),(?<milliseconds>\d{3}) -->")]
    private static partial Regex CueStartRegex();

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }
}
