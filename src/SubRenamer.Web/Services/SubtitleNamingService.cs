using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SubRenamer.Web.Services;

public sealed partial class SubtitleNamingService
{
    public string ComputeLegacyName(
        string videoPath,
        string subtitlePath,
        bool hasDuplicateKey,
        string? extraSuffix = null)
    {
        var videoNameNoExt = Path.GetFileNameWithoutExtension(videoPath);
        var subExt = Path.GetExtension(subtitlePath);
        var subSuffix = "";

        if (hasDuplicateKey)
            subSuffix = LanguageHelper.ExtractLastSegment(subtitlePath) is { } segment ? $".{segment}" : "";
        else if (LanguageHelper.DetectLang(subtitlePath) is { } language)
            subSuffix = $".{language}";

        if (!string.IsNullOrWhiteSpace(extraSuffix))
            subSuffix += $".{SanitizeSuffix(extraSuffix)}";

        return $"{videoNameNoExt}{subSuffix}{subExt}";
    }

    public IReadOnlyList<PlannedSubtitleName> CreateUniqueNames(IReadOnlyList<SubtitleNameInput> inputs)
    {
        var provisional = inputs.Select(CreateProvisionalName).ToList();
        var duplicateNames = provisional
            .GroupBy(x => x.FileName, PathComparer)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(PathComparer);
        var usedNames = new HashSet<string>(PathComparer);
        var results = new List<PlannedSubtitleName>(provisional.Count);

        foreach (var item in provisional)
        {
            var candidate = duplicateNames.Contains(item.FileName)
                ? AddStableDiscriminator(item.FileName, item.StableIdentity)
                : item.FileName;
            var ordinal = 2;
            while (!usedNames.Add(candidate))
                candidate = AddOrdinalDiscriminator(item.FileName, ordinal++);

            results.Add(item with { FileName = candidate });
        }

        return results;
    }

    public void ValidateInput(SubtitleNameInput input) => CreateProvisionalName(input);

    private static PlannedSubtitleName CreateProvisionalName(SubtitleNameInput input)
    {
        var videoBaseName = Path.GetFileNameWithoutExtension(input.VideoPath);
        var extension = Path.GetExtension(input.SubtitlePath).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension))
            throw new ArgumentException("字幕文件缺少扩展名");

        var language = NormalizeLanguage(input.Language)
            ?? LanguageHelper.DetectLang(input.SubtitlePath);
        var suffix = language ?? CreateUnknownSuffix(input.SubtitlePath);
        var fileName = $"{videoBaseName}.{suffix}{extension}";
        var stableIdentity = $"{input.VideoPath}\n{input.SubtitlePath}\n{input.Language}";

        return new PlannedSubtitleName(fileName, language, stableIdentity);
    }

    private static string? NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return null;

        var normalized = LanguageHelper.NormalizeLang(language.Trim());
        if (!SafeSuffixRegex().IsMatch(normalized))
            throw new ArgumentException("语言标记只能包含字母、数字和连字符");

        return normalized;
    }

    private static string CreateUnknownSuffix(string subtitlePath)
    {
        var segment = LanguageHelper.ExtractLastSegment(subtitlePath);
        if (!string.IsNullOrWhiteSpace(segment))
        {
            var sanitized = SanitizeSuffix(segment);
            if (!string.IsNullOrWhiteSpace(sanitized))
                return $"und-{sanitized}";
        }

        return "und";
    }

    private static string SanitizeSuffix(string suffix)
    {
        var sanitized = UnsafeSuffixCharacterRegex()
            .Replace(suffix.Trim().TrimStart('.'), "-")
            .Trim('-');

        return string.IsNullOrWhiteSpace(sanitized) ? "und" : sanitized.ToLowerInvariant();
    }

    private static string AddStableDiscriminator(string fileName, string stableIdentity)
    {
        var extension = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stableIdentity)))
            .ToLowerInvariant()[..8];
        return $"{baseName}-{digest}{extension}";
    }

    private static string AddOrdinalDiscriminator(string fileName, int ordinal)
    {
        var extension = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        return $"{baseName}-{ordinal}{extension}";
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [GeneratedRegex("^[a-zA-Z0-9-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeSuffixRegex();

    [GeneratedRegex("[^a-zA-Z0-9-]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeSuffixCharacterRegex();
}

public record SubtitleNameInput(string VideoPath, string SubtitlePath, string? Language = null);

public record PlannedSubtitleName(string FileName, string? Language, string StableIdentity);
