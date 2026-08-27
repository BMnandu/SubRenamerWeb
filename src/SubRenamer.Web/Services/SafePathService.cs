using System.Text.RegularExpressions;

namespace SubRenamer.Web.Services;

/// <summary>
/// 统一校验媒体目录和上传目录的路径边界，并解析现有符号链接，防止通过目录前缀、.. 或链接逃逸。
/// </summary>
public sealed partial class SafePathService
{
    private readonly string _mediaRoot;
    private readonly string _uploadRoot;

    public SafePathService(AppPaths paths)
    {
        _mediaRoot = NormalizeRoot(paths.MediaDir);
        _uploadRoot = NormalizeRoot(paths.UploadDir);
    }

    public string MediaRoot => _mediaRoot;

    public string EnsureMediaPath(string path) => EnsureWithinRoot(path, _mediaRoot, "媒体目录");

    public bool TryEnsureMediaPath(string path, out string safePath)
    {
        try
        {
            safePath = EnsureMediaPath(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or IOException)
        {
            safePath = string.Empty;
            return false;
        }
    }

    public string EnsureUploadPath(string path) => EnsureWithinRoot(path, _uploadRoot, "上传目录");

    public string EnsureInputPath(string path)
    {
        var fullPath = NormalizePath(path);
        if (IsWithinRoot(fullPath, _mediaRoot) || IsWithinRoot(fullPath, _uploadRoot))
            return fullPath;

        throw new UnauthorizedAccessException("路径越界，仅允许访问媒体目录或上传目录");
    }

    public string ResolveMediaSubdirectory(string? subdirectory)
    {
        if (string.IsNullOrWhiteSpace(subdirectory))
            return _mediaRoot;

        if (Path.IsPathRooted(subdirectory))
            throw new UnauthorizedAccessException("媒体子目录必须使用相对路径");

        return EnsureWithinRoot(Path.Combine(_mediaRoot, subdirectory), _mediaRoot, "媒体目录");
    }

    public string ResolveUploadSessionDirectory(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !SessionIdRegex().IsMatch(sessionId))
            throw new ArgumentException("上传会话 ID 只能包含字母、数字、下划线和连字符，长度为 1-64");

        return EnsureWithinRoot(Path.Combine(_uploadRoot, sessionId), _uploadRoot, "上传目录");
    }

    public string ResolveUploadFile(string sessionId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName is "." or ".."
            || fileName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '\\']) >= 0
            || Path.IsPathRooted(fileName))
        {
            throw new ArgumentException("非法文件名");
        }

        return EnsureWithinRoot(
            Path.Combine(ResolveUploadSessionDirectory(sessionId), fileName),
            _uploadRoot,
            "上传目录");
    }

    private static string EnsureWithinRoot(string path, string root, string rootName)
    {
        var fullPath = NormalizePath(path);
        if (!IsWithinRoot(fullPath, root))
            throw new UnauthorizedAccessException($"路径越界，目标不在{rootName}内");

        return fullPath;
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var physicalPath = ResolvePhysicalPath(path);
        var physicalRoot = ResolvePhysicalPath(root);
        var relative = Path.GetRelativePath(physicalRoot, physicalPath);

        return relative == "."
            || (!Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison));
    }

    private static string ResolvePhysicalPath(string path)
    {
        var fullPath = NormalizePath(path);
        var pathRoot = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException("无法确定路径根目录", nameof(path));
        var current = pathRoot;
        var segments = fullPath[pathRoot.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < segments.Length; index++)
        {
            var candidate = Path.Combine(current, segments[index]);
            FileSystemInfo info = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : new FileInfo(candidate);

            if (info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true)
                    ?? throw new UnauthorizedAccessException($"拒绝无法解析的符号链接：{candidate}");
                current = NormalizePath(target.FullName);
                continue;
            }

            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                for (; index < segments.Length; index++)
                    current = Path.Combine(current, segments[index]);
                break;
            }

            current = candidate;
        }

        return NormalizePath(current);
    }

    private static string NormalizeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("根目录不能为空", nameof(path));

        return NormalizePath(path);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("路径不能为空", nameof(path));

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionIdRegex();
}
