using Microsoft.AspNetCore.Mvc;
using SubRenamer.Core;
using SubRenamer.Web.Models;
using SubRenamer.Web.Services;

namespace SubRenamer.Web.Controllers;

[ApiController]
[Route("api/match")]
public class MatchController(SubtitleNamingService namingService) : ControllerBase
{
    /// <summary>调用 SubRenamer.Core 匹配算法,返回视频↔字幕对应关系(含改名预览)</summary>
    [HttpPost]
    public MatchResponseDto Match([FromBody] MatchRequestDto req)
    {
        var items = new List<MatchItem>();
        foreach (var v in req.Videos ?? new()) items.Add(new MatchItem("", v, ""));
        foreach (var s in req.Subtitles ?? new()) items.Add(new MatchItem("", "", s));

        var options = new MatcherOptions
        {
            VideoRegex = string.IsNullOrEmpty(req.VideoRegex) ? null : req.VideoRegex,
            SubtitleRegex = string.IsNullOrEmpty(req.SubtitleRegex) ? null : req.SubtitleRegex
        };

        var result = Matcher.Execute(items, options);
        var list = result.Select(x => new MatchItemDto(x.Key, x.Video, x.Subtitle)).ToList();

        // 计算改名预览文件名(复用 LanguageHelper.ComputeNewName,与实际改名一致)
        var hasDup = list
            .Where(i => !string.IsNullOrEmpty(i.Video) && !string.IsNullOrEmpty(i.Subtitle))
            .GroupBy(i => i.Key)
            .Any(g => g.Count() > 1);

        var withPreview = list.Select(x =>
            (!string.IsNullOrEmpty(x.Video) && !string.IsNullOrEmpty(x.Subtitle))
                ? x with { Preview = namingService.ComputeLegacyName(x.Video, x.Subtitle, hasDup) }
                : x
        ).ToList();

        return new MatchResponseDto(withPreview);
    }
}
