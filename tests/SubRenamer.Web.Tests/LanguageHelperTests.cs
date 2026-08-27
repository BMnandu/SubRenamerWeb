using SubRenamer.Web.Services;
using Xunit;

namespace SubRenamer.Web.Tests;

public class LanguageHelperTests
{
    [Theory]
    [InlineData("Episode.01.chs.ass", "chs")]
    [InlineData("Episode.01.SC.ass", "chs")]
    [InlineData("Episode.01.zh-Hant.ass", "cht")]
    [InlineData("Episode.01.ja.srt", "jpn")]
    [InlineData("Episode.01.en.srt", "eng")]
    public void DetectLang_NormalizesKnownMarkers(string subtitle, string expected)
    {
        Assert.Equal(expected, LanguageHelper.DetectLang(subtitle));
    }

    [Fact]
    public void ComputeNewName_PreservesDistinctLanguageSuffixes()
    {
        var video = "/media/Show S01E01.mkv";
        var namingService = new SubtitleNamingService();

        var simplified = namingService.ComputeLegacyName(video, "/uploads/Show.01.chs.ass", true);
        var traditional = namingService.ComputeLegacyName(video, "/uploads/Show.01.cht.ass", true);

        Assert.Equal("Show S01E01.chs.ass", simplified);
        Assert.Equal("Show S01E01.cht.ass", traditional);
        Assert.NotEqual(simplified, traditional);
    }
}
