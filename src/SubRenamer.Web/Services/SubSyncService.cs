using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using SubRenamer.Web.Models;

namespace SubRenamer.Web.Services;

/// <summary>调轴任务服务。所有输出先进入任务专用 staging，当前阶段不会写入媒体目录。</summary>
public sealed class SubSyncService : IAsyncDisposable
{
    private readonly SafePathService _safePaths;
    private readonly SyncPlanService _planService;
    private readonly ISyncProcessRunner _processRunner;
    private readonly SyncRuntimeOptions _runtimeOptions;
    private readonly ConcurrentDictionary<string, SyncTaskState> _tasks = new();
    private readonly Channel<SyncTaskState> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;

    public SubSyncService(
        SafePathService safePaths,
        SyncPlanService planService,
        ISyncProcessRunner processRunner,
        SyncRuntimeOptions runtimeOptions)
    {
        _safePaths = safePaths;
        _planService = planService;
        _processRunner = processRunner;
        _runtimeOptions = runtimeOptions;
        Directory.CreateDirectory(_safePaths.WorkRoot);
        CleanupExpiredStagingDirectories();
        _queue = Channel.CreateBounded<SyncTaskState>(new BoundedChannelOptions(runtimeOptions.MaxQueueSize)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = runtimeOptions.MaxConcurrentSyncs == 1
        });
        _workers = Enumerable.Range(0, runtimeOptions.MaxConcurrentSyncs)
            .Select(_ => Task.Run(ProcessQueueAsync))
            .ToArray();
    }

    public SyncTaskCreatedDto CreateTask(SyncTaskRequestDto request)
    {
        ValidateOptions(request.Options);
        CleanupExpiredTasks();

        var plan = _planService.CreatePlan(new SyncPlanRequestDto(request.Items, request.DefaultMode));
        var taskId = Guid.NewGuid().ToString("N");
        var taskDirectory = _safePaths.ResolveTaskDirectory(taskId);
        var outputDirectory = _safePaths.EnsureWorkPath(Path.Combine(taskDirectory, "output"));
        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(_safePaths.EnsureWorkPath(Path.Combine(taskDirectory, "logs")));
        Directory.CreateDirectory(_safePaths.EnsureWorkPath(Path.Combine(taskDirectory, "backup")));

        var task = new SyncTaskState(
            taskId,
            taskDirectory,
            request.Options ?? new SyncExecutionOptionsDto(),
            plan.Items.Select(item => CreateItem(item, outputDirectory)).ToList(),
            _runtimeOptions.MaxLogEntries);
        _tasks[taskId] = task;
        try
        {
            WriteManifest(task);
        }
        catch
        {
            _tasks.TryRemove(taskId, out _);
            TryDeleteTaskDirectory(taskDirectory);
            throw;
        }

        if (!_queue.Writer.TryWrite(task))
        {
            _tasks.TryRemove(taskId, out _);
            TryDeleteTaskDirectory(taskDirectory);
            throw new SyncQueueFullException("调轴队列已满，请稍后重试");
        }

        return new SyncTaskCreatedDto(taskId, SyncTaskStatus.Queued);
    }

    public SyncTaskDto? GetTask(string taskId)
    {
        CleanupExpiredTasks();
        return _tasks.TryGetValue(taskId, out var task) ? task.Snapshot() : null;
    }

    public SyncTaskDto? CancelTask(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            return null;

        task.RequestCancellation();
        return task.Snapshot();
    }

    public async Task<SyncFileOperationResponseDto?> CommitTaskAsync(
        string taskId,
        SyncCommitRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            return null;

        await task.MutationLock.WaitAsync(cancellationToken);
        try
        {
            EnsureTaskCanMutateFiles(task);
            var results = new List<SyncFileOperationItemDto>();
            foreach (var item in task.Items)
            {
                results.Add(await CommitItemAsync(task, item, request.AllowOverwrite, cancellationToken));
                task.RefreshCommitStatus();
                TryWriteManifest(task);
            }

            task.RefreshCommitStatus();
            task.AddLog($"提交处理完成：成功 {CountSucceeded(results)}，冲突 {CountConflicts(results)}");
            TryWriteManifest(task);
            return CreateFileOperationResponse(task, results);
        }
        finally
        {
            task.MutationLock.Release();
        }
    }

    public async Task<SyncFileOperationResponseDto?> RollbackTaskAsync(
        string taskId,
        CancellationToken cancellationToken)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            return null;

        await task.MutationLock.WaitAsync(cancellationToken);
        try
        {
            EnsureTaskCanMutateFiles(task);
            var results = new List<SyncFileOperationItemDto>();
            foreach (var item in task.Items)
            {
                results.Add(await RollbackItemAsync(task, item, cancellationToken));
                task.RefreshCommitStatus();
                TryWriteManifest(task);
            }

            task.RefreshCommitStatus();
            task.AddLog($"回滚处理完成：成功 {CountSucceeded(results)}，冲突 {CountConflicts(results)}");
            TryWriteManifest(task);
            return CreateFileOperationResponse(task, results);
        }
        finally
        {
            task.MutationLock.Release();
        }
    }

    private async Task<SyncFileOperationItemDto> CommitItemAsync(
        SyncTaskState task,
        SyncTaskItemState item,
        bool allowOverwrite,
        CancellationToken cancellationToken)
    {
        string? temporaryTarget = null;
        var temporaryTargetCreated = false;
        if (item.Status == SyncTaskItemStatus.Committed)
        {
            return item.TargetPath is not null
                && File.Exists(item.TargetPath)
                && item.CommittedHash is not null
                && await ComputeHashAsync(item.TargetPath, cancellationToken) == item.CommittedHash
                    ? OperationResult(item, SyncFileOperationStatus.AlreadyCommitted)
                    : OperationResult(item, SyncFileOperationStatus.Conflict, "已提交目标已被移动、删除或修改");
        }
        if (item.Status is not SyncTaskItemStatus.Succeeded and not SyncTaskItemStatus.RolledBack)
            return OperationResult(item, SyncFileOperationStatus.Skipped, "仅 succeeded 或 rolled_back 项可提交");

        try
        {
            var stagingOutput = _safePaths.EnsureWorkPath(item.StagingOutput);
            var targetPath = _safePaths.EnsureMediaPath(
                item.TargetPath ?? throw new InvalidOperationException("计划项缺少目标路径"));
            ValidateOutput(stagingOutput);
            var stagingHash = await ComputeHashAsync(stagingOutput, cancellationToken);
            var targetExisted = File.Exists(targetPath);
            string? backupPath = null;
            string? originalTargetHash = null;

            if (targetExisted && !allowOverwrite)
                return OperationResult(item, SyncFileOperationStatus.Conflict, "目标文件已存在，默认不覆盖");

            if (targetExisted)
            {
                originalTargetHash = await ComputeHashAsync(targetPath, cancellationToken);
                backupPath = _safePaths.EnsureWorkPath(Path.Combine(
                    task.TaskDirectory,
                    "backup",
                    item.CandidateFileName ?? throw new InvalidOperationException("计划项缺少候选文件名")));
                await CopyFileAtomicallyAsync(
                    targetPath,
                    backupPath,
                    overwrite: true,
                    originalTargetHash,
                    cancellationToken);
            }

            var targetDirectory = Path.GetDirectoryName(targetPath)
                ?? throw new InvalidOperationException("无法确定目标目录");
            temporaryTarget = _safePaths.EnsureMediaPath(Path.Combine(
                targetDirectory,
                $".{Path.GetFileName(targetPath)}.subrenamer-{task.TaskId}-{Guid.NewGuid():N}.tmp"));
            await CopyFileAsync(stagingOutput, temporaryTarget, cancellationToken);
            temporaryTargetCreated = true;
            if (await ComputeHashAsync(temporaryTarget, cancellationToken) != stagingHash)
                throw new InvalidOperationException("staging 在提交复制期间发生变化");

            if (targetExisted
                && (originalTargetHash is null
                    || !File.Exists(targetPath)
                    || await ComputeHashAsync(targetPath, cancellationToken) != originalTargetHash))
            {
                DeleteIfExists(temporaryTarget);
                temporaryTargetCreated = false;
                return OperationResult(item, SyncFileOperationStatus.Conflict, "备份后目标文件发生变化，已停止覆盖");
            }

            File.Move(temporaryTarget, targetPath, overwrite: targetExisted);
            temporaryTargetCreated = false;
            item.MarkCommitted(stagingHash, targetExisted, backupPath, originalTargetHash);
            task.AddLog($"已提交 {item.CandidateFileName}");
            return OperationResult(item, SyncFileOperationStatus.Committed);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (temporaryTargetCreated && temporaryTarget is not null)
                DeleteIfExists(temporaryTarget);
            return OperationResult(item, SyncFileOperationStatus.Failed, ex.Message);
        }
    }

    private async Task<SyncFileOperationItemDto> RollbackItemAsync(
        SyncTaskState task,
        SyncTaskItemState item,
        CancellationToken cancellationToken)
    {
        if (item.Status == SyncTaskItemStatus.RolledBack)
            return OperationResult(item, SyncFileOperationStatus.AlreadyRolledBack);
        if (item.Status != SyncTaskItemStatus.Committed)
            return OperationResult(item, SyncFileOperationStatus.Skipped, "仅 committed 项可回滚");

        try
        {
            var targetPath = _safePaths.EnsureMediaPath(
                item.TargetPath ?? throw new InvalidOperationException("计划项缺少目标路径"));
            if (!File.Exists(targetPath)
                || item.CommittedHash is null
                || await ComputeHashAsync(targetPath, cancellationToken) != item.CommittedHash)
            {
                return OperationResult(item, SyncFileOperationStatus.Conflict, "正式目标已被移动、删除或修改，拒绝回滚");
            }

            if (item.TargetExistedBeforeCommit == true)
            {
                if (string.IsNullOrWhiteSpace(item.BackupPath)
                    || string.IsNullOrWhiteSpace(item.OriginalTargetHash))
                {
                    return OperationResult(item, SyncFileOperationStatus.Failed, "覆盖提交缺少备份元数据");
                }

                var backupPath = _safePaths.EnsureWorkPath(item.BackupPath);
                if (!File.Exists(backupPath)
                    || await ComputeHashAsync(backupPath, cancellationToken) != item.OriginalTargetHash)
                {
                    return OperationResult(item, SyncFileOperationStatus.Conflict, "备份文件缺失或已被修改");
                }

                await CopyFileAtomicallyAsync(
                    backupPath,
                    targetPath,
                    overwrite: true,
                    item.OriginalTargetHash,
                    cancellationToken);
            }
            else
            {
                File.Delete(targetPath);
            }

            item.MarkRolledBack();
            task.AddLog($"已回滚 {item.CandidateFileName}");
            return OperationResult(item, SyncFileOperationStatus.RolledBack);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return OperationResult(item, SyncFileOperationStatus.Failed, ex.Message);
        }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var task in _queue.Reader.ReadAllAsync(_shutdown.Token))
                await RunTaskAsync(task);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task RunTaskAsync(SyncTaskState task)
    {
        if (task.Cancellation.IsCancellationRequested)
        {
            task.CancelRemaining();
            TryWriteManifest(task);
            return;
        }

        task.Start();
        task.AddLog("任务开始执行");
        TryWriteManifest(task);

        foreach (var item in task.Items)
        {
            if (task.Cancellation.IsCancellationRequested)
            {
                task.CancelRemaining();
                break;
            }
            if (item.Status == SyncTaskItemStatus.Failed)
                continue;

            await RunItemAsync(task, item);
            TryWriteManifest(task);
        }

        task.Complete();
        task.AddLog($"任务结束：{task.Status.ToString().ToLowerInvariant()}");
        TryWriteManifest(task);
    }

    private async Task RunItemAsync(SyncTaskState task, SyncTaskItemState item)
    {
        item.Start();
        task.AddLog($"开始处理 {Path.GetFileName(item.Subtitle)}");
        var temporaryOutput = CreateTemporaryOutputPath(item.StagingOutput);
        var timeoutSeconds = task.Options.TimeoutSeconds ?? _runtimeOptions.TimeoutSeconds;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(task.Cancellation.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            if (item.Mode == SyncMode.NoSync)
            {
                await CopyToTemporaryOutputAsync(item.Subtitle, temporaryOutput, timeout.Token);
                FinalizeOutput(temporaryOutput, item.StagingOutput);
                item.Succeed(0, 1, SyncQualityStatus.Accepted, []);
                return;
            }

            var reference = item.Mode == SyncMode.SubtitleReference
                ? item.ReferenceSubtitle!
                : item.Video;
            var progress = new Progress<double>(item.SetProgress);
            var result = await _processRunner.RunAsync(
                new SyncProcessRequest(item.Mode, reference, item.Subtitle, temporaryOutput, task.Options),
                progress,
                task.AddLog,
                timeout.Token);
            var qualityReasons = AssessQuality(result, task.Options);

            if (!result.Successful || qualityReasons.Count > 0)
            {
                DeleteIfExists(temporaryOutput);
                item.RejectLowQuality(
                    result.OffsetSeconds,
                    result.FramerateScaleFactor,
                    qualityReasons.Count > 0 ? qualityReasons : ["ffsubsync 判定对齐结果不可信"]);
                return;
            }

            ValidateOutput(temporaryOutput);
            FinalizeOutput(temporaryOutput, item.StagingOutput);
            item.Succeed(
                result.OffsetSeconds,
                result.FramerateScaleFactor,
                SyncQualityStatus.Accepted,
                []);
        }
        catch (OperationCanceledException) when (task.Cancellation.IsCancellationRequested)
        {
            DeleteIfExists(temporaryOutput);
            item.Cancel();
        }
        catch (OperationCanceledException)
        {
            DeleteIfExists(temporaryOutput);
            item.Timeout($"单项执行超过 {timeoutSeconds} 秒");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            DeleteIfExists(temporaryOutput);
            item.Fail(ex.Message);
        }
    }

    private SyncTaskItemState CreateItem(SyncPlanItemDto item, string outputDirectory)
    {
        if (item.Status == SyncPlanItemStatus.Invalid || item.CandidateFileName is null)
        {
            return SyncTaskItemState.Invalid(
                item.ItemId,
                item.Key,
                item.Mode,
                item.Video,
                item.Subtitle,
                item.ReferenceSubtitle,
                item.TargetPath,
                item.Error ?? "调轴计划项无效");
        }

        var stagingOutput = _safePaths.EnsureWorkPath(Path.Combine(outputDirectory, item.CandidateFileName));
        return new SyncTaskItemState(
            item.ItemId,
            item.Key,
            item.Mode,
            item.Video,
            item.Subtitle,
            item.ReferenceSubtitle,
            item.CandidateFileName,
            stagingOutput,
            item.TargetPath);
    }

    private static void ValidateOptions(SyncExecutionOptionsDto? options)
    {
        if (options is null)
            return;
        if (options.TimeoutSeconds is <= 0)
            throw new ArgumentException("timeoutSeconds 必须大于 0");
        if (options.MaxSearchOffsetSeconds <= 0
            || options.MaxQualityOffsetSeconds <= 0
            || options.MaxFramerateDeviation < 0)
        {
            throw new ArgumentException("调轴阈值配置无效");
        }
        if (options.SplitPenalty is <= 0)
            throw new ArgumentException("splitPenalty 必须大于 0");
        if (!string.IsNullOrWhiteSpace(options.ReferenceStream)
            && (options.ReferenceStream.Length > 32
                || options.ReferenceStream.Any(character =>
                    !(char.IsAsciiLetterOrDigit(character) || character is ':' or '.'))))
        {
            throw new ArgumentException("referenceStream 格式无效");
        }
    }

    private static List<string> AssessQuality(SyncProcessResult result, SyncExecutionOptionsDto options)
    {
        if (!options.RejectLowQuality)
            return [];

        var reasons = result.QualityReasons?.ToList() ?? [];
        if (result.OffsetSeconds is { } offset && Math.Abs(offset) > options.MaxQualityOffsetSeconds)
            reasons.Add($"偏移量 {Math.Abs(offset):0.###} 秒超过阈值 {options.MaxQualityOffsetSeconds:0.###} 秒");
        if (result.FramerateScaleFactor is { } scale
            && Math.Abs(scale - 1) > options.MaxFramerateDeviation)
        {
            reasons.Add($"帧率比例偏差 {Math.Abs(scale - 1):0.###} 超过阈值 {options.MaxFramerateDeviation:0.###}");
        }
        return reasons.Distinct(StringComparer.Ordinal).ToList();
    }

    private static async Task CopyToTemporaryOutputAsync(
        string source,
        string temporaryOutput,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var output = new FileStream(temporaryOutput, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private async Task CopyFileAtomicallyAsync(
        string source,
        string destination,
        bool overwrite,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        var temporaryDestination = EnsureTransactionPath($"{destination}.tmp-{Guid.NewGuid():N}");
        var temporaryCreated = false;
        try
        {
            await CopyFileAsync(source, temporaryDestination, cancellationToken);
            temporaryCreated = true;
            if (await ComputeHashAsync(temporaryDestination, cancellationToken) != expectedHash)
                throw new InvalidOperationException("事务复制后的文件哈希不一致");
            File.Move(temporaryDestination, destination, overwrite);
            temporaryCreated = false;
        }
        finally
        {
            if (temporaryCreated)
                DeleteIfExists(temporaryDestination);
        }
    }

    private static async Task CopyFileAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        var destinationCreated = false;
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            destinationCreated = true;
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }
        catch
        {
            if (destinationCreated)
                DeleteIfExists(destination);
            throw;
        }
    }

    private string EnsureTransactionPath(string path)
    {
        try
        {
            return _safePaths.EnsureWorkPath(path);
        }
        catch (UnauthorizedAccessException)
        {
            return _safePaths.EnsureMediaPath(path);
        }
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void EnsureTaskCanMutateFiles(SyncTaskState task)
    {
        if (task.Status is SyncTaskStatus.Queued or SyncTaskStatus.Running)
            throw new SyncTaskNotReadyException("任务尚未执行完成，不能提交或回滚");
    }

    private static SyncFileOperationItemDto OperationResult(
        SyncTaskItemState item,
        SyncFileOperationStatus status,
        string? error = null) =>
        new(item.ItemId, status, item.TargetPath, item.BackupPath, error);

    private static int CountSucceeded(IEnumerable<SyncFileOperationItemDto> items) =>
        items.Count(item => item.Status is
            SyncFileOperationStatus.Committed or
            SyncFileOperationStatus.AlreadyCommitted or
            SyncFileOperationStatus.RolledBack or
            SyncFileOperationStatus.AlreadyRolledBack);

    private static int CountConflicts(IEnumerable<SyncFileOperationItemDto> items) =>
        items.Count(item => item.Status == SyncFileOperationStatus.Conflict);

    private static SyncFileOperationResponseDto CreateFileOperationResponse(
        SyncTaskState task,
        List<SyncFileOperationItemDto> items) =>
        new(
            task.TaskId,
            task.Status,
            CountSucceeded(items),
            CountConflicts(items),
            items.Count(item => item.Status == SyncFileOperationStatus.Failed),
            items);

    private static void ValidateOutput(string output)
    {
        if (!File.Exists(output) || new FileInfo(output).Length == 0)
            throw new InvalidOperationException("ffsubsync 未生成有效输出文件");
    }

    private string CreateTemporaryOutputPath(string stagingOutput)
    {
        var directory = Path.GetDirectoryName(stagingOutput)
            ?? throw new InvalidOperationException("staging 输出目录无效");
        var extension = Path.GetExtension(stagingOutput);
        var fileName = Path.GetFileNameWithoutExtension(stagingOutput);
        return _safePaths.EnsureWorkPath(
            Path.Combine(directory, $"{fileName}.tmp-{Guid.NewGuid():N}{extension}"));
    }

    private static void FinalizeOutput(string temporaryOutput, string stagingOutput)
    {
        ValidateOutput(temporaryOutput);
        File.Move(temporaryOutput, stagingOutput, overwrite: false);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private void WriteManifest(SyncTaskState task)
    {
        var snapshot = task.Snapshot();
        var manifestPath = _safePaths.EnsureWorkPath(Path.Combine(task.TaskDirectory, "manifest.json"));
        var temporaryPath = _safePaths.EnsureWorkPath($"{manifestPath}.tmp");
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot, ManifestJsonOptions));
        File.Move(temporaryPath, manifestPath, overwrite: true);

        var logPath = _safePaths.EnsureWorkPath(Path.Combine(task.TaskDirectory, "logs", "task.log"));
        var temporaryLogPath = _safePaths.EnsureWorkPath($"{logPath}.tmp");
        File.WriteAllLines(temporaryLogPath, snapshot.Logs);
        File.Move(temporaryLogPath, logPath, overwrite: true);
    }

    private void TryWriteManifest(SyncTaskState task)
    {
        try
        {
            WriteManifest(task);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            task.AddLog($"manifest 更新失败：{ex.Message}");
        }
    }

    private void CleanupExpiredTasks()
    {
        var threshold = DateTimeOffset.UtcNow.AddHours(-_runtimeOptions.TaskRetentionHours);
        foreach (var pair in _tasks)
        {
            var snapshot = pair.Value.Snapshot();
            if (snapshot.FinishedAt is null || snapshot.FinishedAt >= threshold)
                continue;
            if (_tasks.TryRemove(pair.Key, out var removed))
                TryDeleteTaskDirectory(removed.TaskDirectory);
        }
    }

    private void CleanupExpiredStagingDirectories()
    {
        var threshold = DateTimeOffset.UtcNow.AddHours(-_runtimeOptions.TaskRetentionHours);
        foreach (var directory in Directory.EnumerateDirectories(_safePaths.WorkRoot))
        {
            var taskId = Path.GetFileName(directory);
            try
            {
                var safeDirectory = _safePaths.ResolveTaskDirectory(taskId);
                if (Directory.GetLastWriteTimeUtc(safeDirectory) < threshold.UtcDateTime)
                    TryDeleteTaskDirectory(safeDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or IOException)
            {
                // 非任务目录不属于本服务，保持不动。
            }
        }
    }

    private static void TryDeleteTaskDirectory(string taskDirectory)
    {
        try
        {
            if (Directory.Exists(taskDirectory))
                Directory.Delete(taskDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        _shutdown.Cancel();
        foreach (var task in _tasks.Values)
            task.RequestCancellation();
        try
        {
            await Task.WhenAll(_workers);
        }
        catch (OperationCanceledException)
        {
        }
        _shutdown.Dispose();
    }

    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    static SubSyncService()
    {
        ManifestJsonOptions.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    }
}

public sealed class SyncQueueFullException(string message) : Exception(message);
public sealed class SyncTaskNotReadyException(string message) : Exception(message);

internal sealed class SyncTaskState
{
    private readonly object _gate = new();
    private readonly int _maxLogEntries;
    private readonly List<string> _logs = [];

    public SyncTaskState(
        string taskId,
        string taskDirectory,
        SyncExecutionOptionsDto options,
        List<SyncTaskItemState> items,
        int maxLogEntries)
    {
        TaskId = taskId;
        TaskDirectory = taskDirectory;
        Options = options;
        Items = items;
        _maxLogEntries = maxLogEntries;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public string TaskId { get; }
    public string TaskDirectory { get; }
    public SyncExecutionOptionsDto Options { get; }
    public List<SyncTaskItemState> Items { get; }
    public CancellationTokenSource Cancellation { get; } = new();
    public SemaphoreSlim MutationLock { get; } = new(1, 1);
    public SyncTaskStatus Status { get; private set; } = SyncTaskStatus.Queued;
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }

    public void Start()
    {
        lock (_gate)
        {
            Status = SyncTaskStatus.Running;
            StartedAt = DateTimeOffset.UtcNow;
        }
    }

    public void RequestCancellation() => Cancellation.Cancel();

    public void CancelRemaining()
    {
        foreach (var item in Items.Where(item => item.Status == SyncTaskItemStatus.Planned))
            item.Cancel();
        Complete();
    }

    public void Complete()
    {
        lock (_gate)
        {
            FinishedAt = DateTimeOffset.UtcNow;
            Status = DetermineFinalStatus();
        }
    }

    public void RefreshCommitStatus()
    {
        lock (_gate)
        {
            var statuses = Items.Select(item => item.Status).ToList();
            var hasExecutionErrors = statuses.Any(status => status is
                SyncTaskItemStatus.RejectedLowQuality or
                SyncTaskItemStatus.Failed or
                SyncTaskItemStatus.Cancelled or
                SyncTaskItemStatus.TimedOut);
            var hasPendingCommit = statuses.Any(status => status is
                SyncTaskItemStatus.Succeeded or
                SyncTaskItemStatus.RolledBack);

            if (hasPendingCommit)
                Status = hasExecutionErrors ? SyncTaskStatus.CompletedWithErrors : SyncTaskStatus.AwaitingCommit;
            else if (statuses.Any(status => status == SyncTaskItemStatus.Committed))
                Status = hasExecutionErrors ? SyncTaskStatus.CompletedWithErrors : SyncTaskStatus.Completed;
        }
    }

    public void AddLog(string message)
    {
        lock (_gate)
        {
            _logs.Add($"[{DateTimeOffset.Now:HH:mm:ss}] {message}");
            if (_logs.Count > _maxLogEntries)
                _logs.RemoveRange(0, _logs.Count - _maxLogEntries);
        }
    }

    public SyncTaskDto Snapshot()
    {
        lock (_gate)
        {
            var items = Items.Select(item => item.Snapshot()).ToList();
            return new SyncTaskDto(
                TaskId,
                Status,
                items.Count,
                items.Count(item => item.Status is not SyncTaskItemStatus.Planned and not SyncTaskItemStatus.Running),
                CreatedAt,
                StartedAt,
                FinishedAt,
                items,
                [.. _logs]);
        }
    }

    private SyncTaskStatus DetermineFinalStatus()
    {
        var statuses = Items.Select(item => item.Status).ToList();
        if (Cancellation.IsCancellationRequested || statuses.Any(status => status == SyncTaskItemStatus.Cancelled))
            return SyncTaskStatus.Cancelled;
        if (statuses.Any(status => status == SyncTaskItemStatus.TimedOut)
            && statuses.All(status => status is SyncTaskItemStatus.TimedOut or SyncTaskItemStatus.Failed))
        {
            return SyncTaskStatus.TimedOut;
        }
        if (statuses.All(status => status == SyncTaskItemStatus.Failed))
            return SyncTaskStatus.Failed;
        if (statuses.All(status => status == SyncTaskItemStatus.Succeeded))
            return SyncTaskStatus.AwaitingCommit;
        return SyncTaskStatus.CompletedWithErrors;
    }
}

internal sealed class SyncTaskItemState
{
    private readonly object _gate = new();

    public SyncTaskItemState(
        string itemId,
        string key,
        SyncMode mode,
        string video,
        string subtitle,
        string? referenceSubtitle,
        string candidateFileName,
        string stagingOutput,
        string? targetPath)
    {
        ItemId = itemId;
        Key = key;
        Mode = mode;
        Video = video;
        Subtitle = subtitle;
        ReferenceSubtitle = referenceSubtitle;
        CandidateFileName = candidateFileName;
        StagingOutput = stagingOutput;
        TargetPath = targetPath;
    }

    public string ItemId { get; }
    public string Key { get; }
    public SyncMode Mode { get; }
    public string Video { get; }
    public string Subtitle { get; }
    public string? ReferenceSubtitle { get; }
    public string? CandidateFileName { get; }
    public string StagingOutput { get; }
    public string? TargetPath { get; }
    public SyncTaskItemStatus Status { get; private set; } = SyncTaskItemStatus.Planned;
    public double Progress { get; private set; }
    public double? OffsetSeconds { get; private set; }
    public double? FramerateScaleFactor { get; private set; }
    public SyncQualityStatus Quality { get; private set; } = SyncQualityStatus.NotEvaluated;
    public List<string> QualityReasons { get; private set; } = [];
    public string? Error { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public bool? TargetExistedBeforeCommit { get; private set; }
    public string? BackupPath { get; private set; }
    public string? CommittedHash { get; private set; }
    public string? OriginalTargetHash { get; private set; }
    public DateTimeOffset? CommittedAt { get; private set; }
    public DateTimeOffset? RolledBackAt { get; private set; }

    public static SyncTaskItemState Invalid(
        string itemId,
        string key,
        SyncMode mode,
        string video,
        string subtitle,
        string? referenceSubtitle,
        string? targetPath,
        string error)
    {
        var item = new SyncTaskItemState(
            itemId,
            key,
            mode,
            video,
            subtitle,
            referenceSubtitle,
            "",
            "",
            targetPath);
        item.Fail(error);
        return item;
    }

    public void Start()
    {
        lock (_gate)
        {
            Status = SyncTaskItemStatus.Running;
            StartedAt = DateTimeOffset.UtcNow;
        }
    }

    public void SetProgress(double progress)
    {
        lock (_gate)
            Progress = Math.Clamp(progress, 0, 1);
    }

    public void Succeed(
        double? offsetSeconds,
        double? framerateScaleFactor,
        SyncQualityStatus quality,
        List<string> qualityReasons) =>
        Finish(SyncTaskItemStatus.Succeeded, offsetSeconds, framerateScaleFactor, quality, qualityReasons, null);

    public void RejectLowQuality(
        double? offsetSeconds,
        double? framerateScaleFactor,
        List<string> qualityReasons) =>
        Finish(
            SyncTaskItemStatus.RejectedLowQuality,
            offsetSeconds,
            framerateScaleFactor,
            SyncQualityStatus.Rejected,
            qualityReasons,
            null);

    public void Fail(string error) =>
        Finish(SyncTaskItemStatus.Failed, null, null, SyncQualityStatus.NotEvaluated, [], error);

    public void Cancel() =>
        Finish(SyncTaskItemStatus.Cancelled, null, null, SyncQualityStatus.NotEvaluated, [], "任务已取消");

    public void Timeout(string error) =>
        Finish(SyncTaskItemStatus.TimedOut, null, null, SyncQualityStatus.NotEvaluated, [], error);

    public void MarkCommitted(
        string committedHash,
        bool targetExistedBeforeCommit,
        string? backupPath,
        string? originalTargetHash)
    {
        lock (_gate)
        {
            Status = SyncTaskItemStatus.Committed;
            TargetExistedBeforeCommit = targetExistedBeforeCommit;
            BackupPath = backupPath;
            CommittedHash = committedHash;
            OriginalTargetHash = originalTargetHash;
            CommittedAt = DateTimeOffset.UtcNow;
            RolledBackAt = null;
            Error = null;
        }
    }

    public void MarkRolledBack()
    {
        lock (_gate)
        {
            Status = SyncTaskItemStatus.RolledBack;
            RolledBackAt = DateTimeOffset.UtcNow;
            Error = null;
        }
    }

    public SyncTaskItemResultDto Snapshot()
    {
        lock (_gate)
        {
            return new SyncTaskItemResultDto(
                ItemId,
                Key,
                Mode,
                Status,
                Video,
                Subtitle,
                ReferenceSubtitle,
                CandidateFileName,
                string.IsNullOrWhiteSpace(StagingOutput) ? null : StagingOutput,
                TargetPath,
                Progress,
                OffsetSeconds,
                FramerateScaleFactor,
                Quality,
                [.. QualityReasons],
                Error,
                StartedAt,
                FinishedAt,
                TargetExistedBeforeCommit,
                BackupPath,
                CommittedHash,
                OriginalTargetHash,
                CommittedAt,
                RolledBackAt);
        }
    }

    private void Finish(
        SyncTaskItemStatus status,
        double? offsetSeconds,
        double? framerateScaleFactor,
        SyncQualityStatus quality,
        List<string> qualityReasons,
        string? error)
    {
        lock (_gate)
        {
            Status = status;
            Progress = 1;
            OffsetSeconds = offsetSeconds;
            FramerateScaleFactor = framerateScaleFactor;
            Quality = quality;
            QualityReasons = qualityReasons;
            Error = error;
            FinishedAt = DateTimeOffset.UtcNow;
        }
    }
}
