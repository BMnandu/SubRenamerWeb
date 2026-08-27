using SubRenamer.Web.Models;
using SubRenamer.Web.Services;
using Xunit;

namespace SubRenamer.Web.Tests;

public sealed class SubSyncServiceTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _mediaRoot;
    private readonly string _uploadRoot;
    private readonly string _workRoot;
    private readonly SafePathService _safePaths;
    private readonly SyncPlanService _planService;

    public SubSyncServiceTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"subrenamer-sync-task-{Guid.NewGuid():N}");
        _mediaRoot = Path.Combine(_testRoot, "media");
        _uploadRoot = Path.Combine(_testRoot, "uploads");
        _workRoot = Path.Combine(_testRoot, "work");
        Directory.CreateDirectory(_mediaRoot);
        Directory.CreateDirectory(_uploadRoot);
        Directory.CreateDirectory(_workRoot);
        _safePaths = new SafePathService(new AppPaths(_mediaRoot, _uploadRoot, _workRoot));
        _planService = new SyncPlanService(_safePaths, new SubtitleNamingService());
    }

    [Fact]
    public async Task NoSync_WritesOnlyToStagingAndPreservesInputs()
    {
        var video = CreateFile(_mediaRoot, "Show S01E01.mkv", "video");
        var subtitle = CreateFile(_uploadRoot, "Subtitle.01.chs.ass", "subtitle");
        await using var service = CreateService(new StubRunner());

        var created = service.CreateTask(new SyncTaskRequestDto([
            new("01", video, subtitle, Mode: SyncMode.NoSync)
        ]));
        var task = await WaitForTerminalAsync(service, created.TaskId);

        Assert.Equal(SyncTaskStatus.AwaitingCommit, task.Status);
        Assert.Equal(SyncTaskItemStatus.Succeeded, task.Items[0].Status);
        Assert.Equal("subtitle", File.ReadAllText(task.Items[0].StagingOutput!));
        Assert.Equal("subtitle", File.ReadAllText(subtitle));
        Assert.False(File.Exists(task.Items[0].TargetPath));
        Assert.True(File.Exists(Path.Combine(_workRoot, created.TaskId, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(_workRoot, created.TaskId, "logs", "task.log")));
    }

    [Fact]
    public async Task SuccessfulSync_ReturnsStructuredMetrics()
    {
        var video = CreateFile(_mediaRoot, "Show S01E01.mkv", "video");
        var subtitle = CreateFile(_uploadRoot, "Subtitle.01.ass", "subtitle");
        var runner = new StubRunner(async (request, _, _, _) =>
        {
            await File.WriteAllTextAsync(request.Output, "synced");
            return new SyncProcessResult(true, 1.25, 1.001);
        });
        await using var service = CreateService(runner);

        var created = service.CreateTask(new SyncTaskRequestDto([
            new("01", video, subtitle)
        ]));
        var task = await WaitForTerminalAsync(service, created.TaskId);

        Assert.Equal(SyncTaskStatus.AwaitingCommit, task.Status);
        Assert.Equal(1.25, task.Items[0].OffsetSeconds);
        Assert.Equal(1.001, task.Items[0].FramerateScaleFactor);
        Assert.Equal(SyncQualityStatus.Accepted, task.Items[0].Quality);
        Assert.Equal("synced", File.ReadAllText(task.Items[0].StagingOutput!));
    }

    [Fact]
    public async Task SyncRunner_TemporaryOutputPreservesSubtitleExtension()
    {
        var video = CreateFile(_mediaRoot, "Show S01E01.mkv", "video");
        var subtitle = CreateFile(_uploadRoot, "Subtitle.01.ass", "subtitle");
        var runner = new StubRunner(async (request, _, _, _) =>
        {
            Assert.Equal(".ass", Path.GetExtension(request.Output));
            Assert.Contains(".tmp-", Path.GetFileNameWithoutExtension(request.Output));
            await File.WriteAllTextAsync(request.Output, "synced");
            return new SyncProcessResult(true, 0, 1);
        });
        await using var service = CreateService(runner);

        var created = service.CreateTask(new SyncTaskRequestDto([
            new("01", video, subtitle)
        ]));
        var task = await WaitForTerminalAsync(service, created.TaskId);

        Assert.Equal(SyncTaskStatus.AwaitingCommit, task.Status);
        Assert.Equal(SyncTaskItemStatus.Succeeded, task.Items[0].Status);
    }

    [Fact]
    public async Task LowQualityResult_IsRejectedAndOutputIsRemoved()
    {
        var video = CreateFile(_mediaRoot, "Show S01E01.mkv", "video");
        var subtitle = CreateFile(_uploadRoot, "Subtitle.01.ass", "subtitle");
        var runner = new StubRunner(async (request, _, _, _) =>
        {
            await File.WriteAllTextAsync(request.Output, "untrusted");
            return new SyncProcessResult(false, 45, 1, QualityReasons: ["offset too large"]);
        });
        await using var service = CreateService(runner);

        var created = service.CreateTask(new SyncTaskRequestDto([
            new("01", video, subtitle)
        ]));
        var task = await WaitForTerminalAsync(service, created.TaskId);

        Assert.Equal(SyncTaskStatus.CompletedWithErrors, task.Status);
        Assert.Equal(SyncTaskItemStatus.RejectedLowQuality, task.Items[0].Status);
        Assert.Equal(SyncQualityStatus.Rejected, task.Items[0].Quality);
        Assert.NotEmpty(task.Items[0].QualityReasons);
        Assert.Contains("offset too large", task.Items[0].QualityReasons);
        Assert.False(File.Exists(task.Items[0].StagingOutput));
        Assert.False(File.Exists(task.Items[0].TargetPath));
    }

    [Fact]
    public async Task FailedItem_DoesNotStopFollowingItem()
    {
        var firstVideo = CreateFile(_mediaRoot, "Show S01E01.mkv", "video");
        var secondVideo = CreateFile(_mediaRoot, "Show S01E02.mkv", "video");
        var firstSubtitle = CreateFile(_uploadRoot, "Subtitle.01.ass", "one");
        var secondSubtitle = CreateFile(_uploadRoot, "Subtitle.02.ass", "two");
        var calls = 0;
        var runner = new StubRunner(async (request, _, _, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new InvalidOperationException("模拟失败");
            await File.WriteAllTextAsync(request.Output, "synced");
            return new SyncProcessResult(true, 0.5, 1);
        });
        await using var service = CreateService(runner);

        var created = service.CreateTask(new SyncTaskRequestDto([
            new("01", firstVideo, firstSubtitle),
            new("02", secondVideo, secondSubtitle)
        ]));
        var task = await WaitForTerminalAsync(service, created.TaskId);

        Assert.Equal(SyncTaskStatus.CompletedWithErrors, task.Status);
        Assert.Equal(SyncTaskItemStatus.Failed, task.Items[0].Status);
        Assert.Equal(SyncTaskItemStatus.Succeeded, task.Items[1].Status);
    }

    [Fact]
    public async Task Timeout_StopsItemWithoutWritingMediaOutput()
    {
        var video = CreateFile(_mediaRoot, "Show S01E01.mkv", "video");
        var subtitle = CreateFile(_uploadRoot, "Subtitle.01.ass", "subtitle");
        var runner = new StubRunner(async (_, _, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new SyncProcessResult(true, 0, 1);
        });
        await using var service = CreateService(runner, timeoutSeconds: 1);

        var created = service.CreateTask(new SyncTaskRequestDto([
            new("01", video, subtitle)
        ]));
        var task = await WaitForTerminalAsync(service, created.TaskId, TimeSpan.FromSeconds(5));

        Assert.Equal(SyncTaskStatus.TimedOut, task.Status);
        Assert.Equal(SyncTaskItemStatus.TimedOut, task.Items[0].Status);
        Assert.False(File.Exists(task.Items[0].TargetPath));
    }

    [Fact]
    public async Task Cancel_StopsRunningItemAndMarksRemainingItems()
    {
        var video = CreateFile(_mediaRoot, "Show S01E01.mkv", "video");
        var subtitle = CreateFile(_uploadRoot, "Subtitle.01.ass", "subtitle");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new StubRunner(async (_, _, _, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new SyncProcessResult(true, 0, 1);
        });
        await using var service = CreateService(runner);
        var created = service.CreateTask(new SyncTaskRequestDto([
            new("01", video, subtitle),
            new("01", video, subtitle)
        ]));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        service.CancelTask(created.TaskId);
        var task = await WaitForTerminalAsync(service, created.TaskId);

        Assert.Equal(SyncTaskStatus.Cancelled, task.Status);
        Assert.All(task.Items, item => Assert.Equal(SyncTaskItemStatus.Cancelled, item.Status));
    }

    [Fact]
    public async Task FullQueue_RejectsAdditionalTask()
    {
        var video = CreateFile(_mediaRoot, "Show S01E01.mkv", "video");
        var subtitle = CreateFile(_uploadRoot, "Subtitle.01.ass", "subtitle");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new StubRunner(async (request, _, _, cancellationToken) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            await File.WriteAllTextAsync(request.Output, "synced", cancellationToken);
            return new SyncProcessResult(true, 0, 1);
        });
        await using var service = CreateService(runner, maxQueueSize: 1);
        var request = new SyncTaskRequestDto([new("01", video, subtitle)]);
        service.CreateTask(request);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        service.CreateTask(request);

        Assert.Throws<SyncQueueFullException>(() => service.CreateTask(request));
        release.SetResult();
    }

    [Fact]
    public async Task Startup_RemovesOnlyExpiredTaskDirectories()
    {
        var expiredTask = Path.Combine(_workRoot, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var recentTask = Path.Combine(_workRoot, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var unrelated = Path.Combine(_workRoot, "keep-me");
        Directory.CreateDirectory(expiredTask);
        Directory.CreateDirectory(recentTask);
        Directory.CreateDirectory(unrelated);
        Directory.SetLastWriteTimeUtc(expiredTask, DateTime.UtcNow.AddHours(-2));

        await using var service = new SubSyncService(
            _safePaths,
            _planService,
            new StubRunner(),
            new SyncRuntimeOptions(1, 10, 30, 1, 20, "python3"));

        Assert.False(Directory.Exists(expiredTask));
        Assert.True(Directory.Exists(recentTask));
        Assert.True(Directory.Exists(unrelated));
    }

    [Fact]
    public async Task CommitAndRollback_NewTargetAreIdempotent()
    {
        var video = CreateFile(_mediaRoot, "Show S01E01.mkv", "video");
        var subtitle = CreateFile(_uploadRoot, "Subtitle.01.chs.ass", "subtitle");
        await using var service = CreateService(new StubRunner());
        var created = service.CreateTask(new SyncTaskRequestDto([
            new("01", video, subtitle, Mode: SyncMode.NoSync)
        ]));
        var staged = await WaitForTerminalAsync(service, created.TaskId);
        var target = staged.Items[0].TargetPath!;

        var committed = await service.CommitTaskAsync(
            created.TaskId,
            new SyncCommitRequestDto(),
            CancellationToken.None);
        var repeatedCommit = await service.CommitTaskAsync(
            created.TaskId,
            new SyncCommitRequestDto(),
            CancellationToken.None);

        Assert.Equal(SyncFileOperationStatus.Committed, committed!.Items[0].Status);
        Assert.Equal(SyncFileOperationStatus.AlreadyCommitted, repeatedCommit!.Items[0].Status);
        Assert.Equal("subtitle", File.ReadAllText(target));
        Assert.Equal(SyncTaskStatus.Completed, service.GetTask(created.TaskId)!.Status);

        var rolledBack = await service.RollbackTaskAsync(created.TaskId, CancellationToken.None);
        var repeatedRollback = await service.RollbackTaskAsync(created.TaskId, CancellationToken.None);

        Assert.Equal(SyncFileOperationStatus.RolledBack, rolledBack!.Items[0].Status);
        Assert.Equal(SyncFileOperationStatus.AlreadyRolledBack, repeatedRollback!.Items[0].Status);
        Assert.False(File.Exists(target));
        Assert.Equal(SyncTaskStatus.AwaitingCommit, service.GetTask(created.TaskId)!.Status);
    }

    [Fact]
    public async Task Commit_DefaultConflictDoesNotOverwriteTarget()
    {
        var video = CreateFile(_mediaRoot, "Show S01E01.mkv", "video");
        var subtitle = CreateFile(_uploadRoot, "Subtitle.01.chs.ass", "new");
        var target = CreateFile(_mediaRoot, "Show S01E01.chs.ass", "existing");
        await using var service = CreateService(new StubRunner());
        var created = service.CreateTask(new SyncTaskRequestDto([
            new("01", video, subtitle, Mode: SyncMode.NoSync)
        ]));
        await WaitForTerminalAsync(service, created.TaskId);

        var result = await service.CommitTaskAsync(
            created.TaskId,
            new SyncCommitRequestDto(),
            CancellationToken.None);

        Assert.Equal(1, result!.Conflicts);
        Assert.Equal(SyncFileOperationStatus.Conflict, result.Items[0].Status);
        Assert.Equal("existing", File.ReadAllText(target));
        Assert.Equal(SyncTaskItemStatus.Succeeded, service.GetTask(created.TaskId)!.Items[0].Status);
    }

    [Fact]
    public async Task Commit_WithOverwriteBacksUpAndRollbackRestoresOriginal()
    {
        var video = CreateFile(_mediaRoot, "Show S01E01.mkv", "video");
        var subtitle = CreateFile(_uploadRoot, "Subtitle.01.chs.ass", "new");
        var target = CreateFile(_mediaRoot, "Show S01E01.chs.ass", "original");
        await using var service = CreateService(new StubRunner());
        var created = service.CreateTask(new SyncTaskRequestDto([
            new("01", video, subtitle, Mode: SyncMode.NoSync)
        ]));
        await WaitForTerminalAsync(service, created.TaskId);

        var committed = await service.CommitTaskAsync(
            created.TaskId,
            new SyncCommitRequestDto(AllowOverwrite: true),
            CancellationToken.None);
        var committedTask = service.GetTask(created.TaskId)!;

        Assert.Equal(SyncFileOperationStatus.Committed, committed!.Items[0].Status);
        Assert.Equal("new", File.ReadAllText(target));
        Assert.Equal("original", File.ReadAllText(committedTask.Items[0].BackupPath!));
        Assert.True(committedTask.Items[0].TargetExistedBeforeCommit);
        var manifest = File.ReadAllText(Path.Combine(_workRoot, created.TaskId, "manifest.json"));
        Assert.Contains(committedTask.Items[0].CommittedHash!, manifest);
        Assert.Contains(committedTask.Items[0].OriginalTargetHash!, manifest);

        var rolledBack = await service.RollbackTaskAsync(created.TaskId, CancellationToken.None);

        Assert.Equal(SyncFileOperationStatus.RolledBack, rolledBack!.Items[0].Status);
        Assert.Equal("original", File.ReadAllText(target));
        Assert.Equal("new", File.ReadAllText(committedTask.Items[0].StagingOutput!));
    }

    [Fact]
    public async Task Rollback_RejectsTargetModifiedAfterCommit()
    {
        var video = CreateFile(_mediaRoot, "Show S01E01.mkv", "video");
        var subtitle = CreateFile(_uploadRoot, "Subtitle.01.chs.ass", "new");
        await using var service = CreateService(new StubRunner());
        var created = service.CreateTask(new SyncTaskRequestDto([
            new("01", video, subtitle, Mode: SyncMode.NoSync)
        ]));
        var staged = await WaitForTerminalAsync(service, created.TaskId);
        var target = staged.Items[0].TargetPath!;
        await service.CommitTaskAsync(created.TaskId, new SyncCommitRequestDto(), CancellationToken.None);
        File.WriteAllText(target, "external-change");

        var result = await service.RollbackTaskAsync(created.TaskId, CancellationToken.None);

        Assert.Equal(1, result!.Conflicts);
        Assert.Equal("external-change", File.ReadAllText(target));
        Assert.Equal(SyncTaskItemStatus.Committed, service.GetTask(created.TaskId)!.Items[0].Status);
    }

    [Fact]
    public async Task Rollback_RejectsBackupModifiedAfterOverwriteCommit()
    {
        var video = CreateFile(_mediaRoot, "Show S01E01.mkv", "video");
        var subtitle = CreateFile(_uploadRoot, "Subtitle.01.chs.ass", "new");
        var target = CreateFile(_mediaRoot, "Show S01E01.chs.ass", "original");
        await using var service = CreateService(new StubRunner());
        var created = service.CreateTask(new SyncTaskRequestDto([
            new("01", video, subtitle, Mode: SyncMode.NoSync)
        ]));
        await WaitForTerminalAsync(service, created.TaskId);
        await service.CommitTaskAsync(
            created.TaskId,
            new SyncCommitRequestDto(AllowOverwrite: true),
            CancellationToken.None);
        var committedTask = service.GetTask(created.TaskId)!;
        File.WriteAllText(committedTask.Items[0].BackupPath!, "modified-backup");

        var result = await service.RollbackTaskAsync(created.TaskId, CancellationToken.None);

        Assert.Equal(1, result!.Conflicts);
        Assert.Equal("new", File.ReadAllText(target));
        Assert.Equal(SyncTaskItemStatus.Committed, service.GetTask(created.TaskId)!.Items[0].Status);
    }

    [Fact]
    public async Task Commit_SkipsRejectedLowQualityItem()
    {
        var video = CreateFile(_mediaRoot, "Show S01E01.mkv", "video");
        var subtitle = CreateFile(_uploadRoot, "Subtitle.01.ass", "subtitle");
        var runner = new StubRunner(async (request, _, _, _) =>
        {
            await File.WriteAllTextAsync(request.Output, "untrusted");
            return new SyncProcessResult(false, 45, 1, QualityReasons: ["low quality"]);
        });
        await using var service = CreateService(runner);
        var created = service.CreateTask(new SyncTaskRequestDto([
            new("01", video, subtitle)
        ]));
        var task = await WaitForTerminalAsync(service, created.TaskId);

        var result = await service.CommitTaskAsync(
            created.TaskId,
            new SyncCommitRequestDto(AllowOverwrite: true),
            CancellationToken.None);

        Assert.Equal(SyncFileOperationStatus.Skipped, result!.Items[0].Status);
        Assert.False(File.Exists(task.Items[0].TargetPath));
    }

    [Fact]
    public async Task Commit_RejectsTaskThatIsStillRunning()
    {
        var video = CreateFile(_mediaRoot, "Show S01E01.mkv", "video");
        var subtitle = CreateFile(_uploadRoot, "Subtitle.01.ass", "subtitle");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new StubRunner(async (request, _, _, cancellationToken) =>
        {
            started.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            await File.WriteAllTextAsync(request.Output, "synced", cancellationToken);
            return new SyncProcessResult(true, 0, 1);
        });
        await using var service = CreateService(runner);
        var created = service.CreateTask(new SyncTaskRequestDto([
            new("01", video, subtitle)
        ]));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<SyncTaskNotReadyException>(() =>
            service.CommitTaskAsync(created.TaskId, new SyncCommitRequestDto(), CancellationToken.None));

        release.SetResult();
        await WaitForTerminalAsync(service, created.TaskId);
    }

    private SubSyncService CreateService(
        ISyncProcessRunner runner,
        int timeoutSeconds = 30,
        int maxQueueSize = 10) =>
        new(
            _safePaths,
            _planService,
            runner,
            new SyncRuntimeOptions(1, maxQueueSize, timeoutSeconds, 24, 20, "python3"));

    private static async Task<SyncTaskDto> WaitForTerminalAsync(
        SubSyncService service,
        string taskId,
        TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(3));
        while (true)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            var task = service.GetTask(taskId) ?? throw new InvalidOperationException("任务不存在");
            if (task.FinishedAt is not null)
                return task;
            await Task.Delay(20, cancellation.Token);
        }
    }

    private static string CreateFile(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private sealed class StubRunner(
        Func<SyncProcessRequest, IProgress<double>, Action<string>, CancellationToken, Task<SyncProcessResult>>? run = null)
        : ISyncProcessRunner
    {
        public Task<SyncProcessResult> RunAsync(
            SyncProcessRequest request,
            IProgress<double> progress,
            Action<string> log,
            CancellationToken cancellationToken) =>
            run?.Invoke(request, progress, log, cancellationToken)
            ?? throw new InvalidOperationException("此测试不应调用进程执行器");
    }
}
