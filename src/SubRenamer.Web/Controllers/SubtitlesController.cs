using Microsoft.AspNetCore.Mvc;
using SubRenamer.Web.Models;
using SubRenamer.Web.Services;

namespace SubRenamer.Web.Controllers;

[ApiController]
[Route("api/subtitles")]
public class SubtitlesController(UploadService uploadService) : ControllerBase
{
    /// <summary>上传字幕文件(支持批量),返回容器内存储路径</summary>
    [HttpPost("upload")]
    [RequestSizeLimit(200_000_000)]
    public async Task<ActionResult<List<UploadResultDto>>> Upload(
        List<IFormFile> files,
        [FromQuery] string? sessionId)
    {
        if (files == null || files.Count == 0)
            return BadRequest(new ErrorResponseDto("未收到文件"));

        sessionId ??= Guid.NewGuid().ToString("N")[..8];
        var results = new List<UploadResultDto>();
        try
        {
            foreach (var f in files)
                results.Add(await uploadService.SaveAsync(f, sessionId));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ErrorResponseDto(ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponseDto(ex.Message));
        }

        Response.Headers["X-Session-Id"] = sessionId;
        return Ok(results);
    }
}
