using System.Text.RegularExpressions;

namespace SubRenamer.Web.Services;

/// <summary>
/// 字幕语言标记识别。从字幕文件名提取语言后缀,标准化为 Emby 友好的代码。
/// 支持纯标记(chs/cht/zh)和嵌入标记(LKSub-SC、Sub:SC 等)。
/// 参考原 SubRenamer 的 KeepLangExt,增强识别字幕组命名(如 LoliHouse 的 LKSub-SC)。
/// </summary>
public static class LanguageHelper
{
    // 语言标记正则(长标记优先避免被短标记截断,字母边界匹配避免误识别)
    private static readonly Regex LangRegex = new(
        @"(?<![A-Za-z])(zh-Hans|zh-Hant|zh-CN|zh-TW|gb2312|big5|chs|cht|jpn|eng|kor|sc|tc|jp|ja|en|ko|zh|gb)(?![A-Za-z])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 标记 → 标准输出(Emby 友好)
    private static readonly Dictionary<string, string> StandardMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // 简中 → chs
        ["chs"] = "chs",
        ["sc"] = "chs",
        ["zh-CN"] = "chs",
        ["zh-Hans"] = "chs",
        ["gb"] = "chs",
        ["gb2312"] = "chs",
        // 繁中 → cht
        ["cht"] = "cht",
        ["tc"] = "cht",
        ["zh-TW"] = "cht",
        ["zh-Hant"] = "cht",
        ["big5"] = "cht",
        // 通用中文 → zh
        ["zh"] = "zh",
        // 日语 → jpn
        ["jp"] = "jpn",
        ["ja"] = "jpn",
        ["jpn"] = "jpn",
        // 英语 → eng
        ["en"] = "eng",
        ["eng"] = "eng",
        // 韩语 → kor
        ["kor"] = "kor",
        ["ko"] = "kor",
    };

    /// <summary>
    /// 从字幕文件名提取语言标记,返回标准化语言代码(Emby 友好)。
    /// 支持 chs/cht/sc/tc/zh-CN 等纯标记,以及 LKSub-SC 这类嵌入标记(取 SC → chs)。
    /// </summary>
    public static string? DetectLang(string subtitlePath)
    {
        var name = Path.GetFileNameWithoutExtension(subtitlePath);
        var parts = name.Split('.');
        if (parts.Length < 2) return null;
        var last = parts[^1]; // 最后一部分,如 LKSub-SC、chs、zh-CN
        var match = LangRegex.Match(last);
        if (!match.Success) return null;
        var marker = match.Groups[1].Value;
        return StandardMap.TryGetValue(marker, out var std) ? std : marker;
    }

    public static string NormalizeLang(string language) =>
        StandardMap.TryGetValue(language, out var normalized)
            ? normalized
            : language.ToLowerInvariant();

    /// <summary>
    /// 提取字幕文件名最后一部分(不校验是否语言标记)。
    /// 用于一对多场景避免文件名冲突。
    /// </summary>
    public static string? ExtractLastSegment(string subtitlePath)
    {
        var name = Path.GetFileNameWithoutExtension(subtitlePath);
        var parts = name.Split('.');
        return parts.Length < 2 ? null : parts[^1];
    }

    public static bool HasLangMarker(string subtitlePath) => DetectLang(subtitlePath) != null;

    /// <summary>
    /// 计算改名后的字幕文件名(共享逻辑,RenameService 实际改名与预览都用此)。
    /// 一对多时强制提取末尾段避免冲突,单语言只认已知语言标记。
    /// extraSuffix 为用户手动追加的后缀(可选)。
    /// </summary>
}
