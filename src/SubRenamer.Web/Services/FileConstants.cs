namespace SubRenamer.Web.Services;

/// <summary>
/// 视频/字幕文件扩展名识别,与原 SubRenamer 桌面版保持一致。
/// </summary>
public static class FileConstants
{
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mkv", "mp4", "flv", "avi", "mov", "rmvb", "wmv", "mpg", "avs", "m4v", "ts",
        "3gp", "asf", "divx", "f4v", "m2ts", "mpeg", "mts", "ogv", "qt", "rm", "rv",
        "swf", "vob", "webm", "xvid", "strm"
    };

    public static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // 常见字幕格式
        "srt", "ass", "ssa", "sub", "vtt", "smi", "lrc",
        // 其他字幕格式
        "ttxt", "sbv", "cap", "dfxp", "ttml", "mpl2", "aqt", "jss", "psb", "pjs", "stl", "usf"
        // 注:移除了 json/xml/txt/idx,这些扩展名在媒体目录常被非字幕文件占用导致误识别
    };

    public static bool IsVideo(string path)
        => VideoExtensions.Contains(Path.GetExtension(path).TrimStart('.'));

    public static bool IsSubtitle(string path)
        => SubtitleExtensions.Contains(Path.GetExtension(path).TrimStart('.'));

    public static string GetExtension(string path)
        => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
}
