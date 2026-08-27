using System.Collections.Concurrent;
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
        var temporaryOutput = _safePaths.EnsureWorkPath($"{item.StagingOutput}.tmp-{Guid.NewGuid():N}");
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

    private static void ValidateOutput(string output)
    {
        if (!File.Exists(output) || new FileInfo(output).Length == 0)
            throw new InvalidOperationException("ffsubsync 未生成有效输出文件");
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
                FinishedAt);
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
