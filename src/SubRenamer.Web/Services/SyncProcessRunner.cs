using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SubRenamer.Web.Models;

namespace SubRenamer.Web.Services;

public interface ISyncProcessRunner
{
    Task<SyncProcessResult> RunAsync(
        SyncProcessRequest request,
        IProgress<double> progress,
        Action<string> log,
        CancellationToken cancellationToken);
}

public sealed record SyncProcessRequest(
    SyncMode Mode,
    string Reference,
    string Subtitle,
    string Output,
    SyncExecutionOptionsDto Options);

public sealed record SyncProcessResult(
    bool Successful,
    double? OffsetSeconds,
    double? FramerateScaleFactor,
    string? Error = null,
    IReadOnlyList<string>? QualityReasons = null);

public sealed class FfsubsyncProcessRunner(SyncRuntimeOptions runtimeOptions) : ISyncProcessRunner
{
    private static readonly string WrapperPath =
        Path.Combine(AppContext.BaseDirectory, "scripts", "sync_wrapper.py");

    public async Task<SyncProcessResult> RunAsync(
        SyncProcessRequest request,
        IProgress<double> progress,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = runtimeOptions.PythonExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        AddArguments(startInfo.ArgumentList, request);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("无法启动 ffsubsync 进程");

        SyncProcessResult? result = null;
        string? protocolError = null;
        var stdoutTask = ReadStdoutAsync(process, progress, value => result = value, value => protocolError = value);
        var stderrTask = ReadStderrAsync(process, log);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdoutTask, stderrTask);
            throw;
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException(protocolError ?? $"ffsubsync 退出码 {process.ExitCode}");
        if (result is null)
            throw new InvalidOperationException(protocolError ?? "ffsubsync 未返回结构化结果");

        return result;
    }

    private static void AddArguments(ICollection<string> arguments, SyncProcessRequest request)
    {
        arguments.Add(WrapperPath);
        arguments.Add(request.Reference);
        arguments.Add(request.Subtitle);
        arguments.Add(request.Output);
        arguments.Add("--max-offset-seconds");
        arguments.Add(request.Options.MaxSearchOffsetSeconds.ToString(CultureInfo.InvariantCulture));
        arguments.Add("--min-score");
        arguments.Add(request.Options.MinScore.ToString(CultureInfo.InvariantCulture));
        arguments.Add("--quality-max-offset-seconds");
        arguments.Add(request.Options.MaxQualityOffsetSeconds.ToString(CultureInfo.InvariantCulture));
        arguments.Add("--max-framerate-deviation");
        arguments.Add(request.Options.MaxFramerateDeviation.ToString(CultureInfo.InvariantCulture));

        if (request.Options.RejectLowQuality)
            arguments.Add("--skip-sync-on-low-quality");
        if (!string.IsNullOrWhiteSpace(request.Options.ReferenceStream))
        {
            arguments.Add("--reference-stream");
            arguments.Add(request.Options.ReferenceStream);
        }
        if (request.Mode == SyncMode.VideoSplit)
        {
            arguments.Add("--split-penalty");
            arguments.Add((request.Options.SplitPenalty ?? 5).ToString(CultureInfo.InvariantCulture));
        }
    }

    private static async Task ReadStdoutAsync(
        Process process,
        IProgress<double> progress,
        Action<SyncProcessResult> setResult,
        Action<string> setError)
    {
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = root.GetProperty("type").GetString();
                if (type == "progress" && root.TryGetProperty("fraction", out var fraction))
                    progress.Report(Math.Clamp(fraction.GetDouble(), 0, 1));
                else if (type == "result")
                    setResult(new SyncProcessResult(
                        root.GetProperty("successful").GetBoolean(),
                        ReadNullableDouble(root, "offset_seconds"),
                        ReadNullableDouble(root, "framerate_scale_factor"),
                        QualityReasons: ReadStringArray(root, "quality_reasons")));
                else if (type == "error")
                    setError(root.GetProperty("message").GetString() ?? "ffsubsync 错误");
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                setError("ffsubsync 返回了无法解析的协议数据");
            }
        }
    }

    private static async Task ReadStderrAsync(Process process, Action<string> log)
    {
        while (await process.StandardError.ReadLineAsync() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
                log(line);
        }
    }

    private static double? ReadNullableDouble(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return value.GetDouble();
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToList();
    }
}
