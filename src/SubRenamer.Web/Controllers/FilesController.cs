using Microsoft.AspNetCore.Mvc;
using SubRenamer.Web.Models;
using SubRenamer.Web.Services;

namespace SubRenamer.Web.Controllers;

[ApiController]
[Route("api/files")]
public class FilesController(FileScanService scanService) : ControllerBase
{
    /// <summary>扫描挂载目录(可指定子目录),返回视频与字幕文件列表</summary>
    [HttpGet("scan")]
    public ActionResult<ScanResultDto> Scan([FromQuery] string? dir)
    {
        try { return Ok(scanService.Scan(dir)); }
        catch (UnauthorizedAccessException ex) { return Problem(ex.Message, statusCode: 403); }
        catch (DirectoryNotFoundException) { return NotFound(new ErrorResponseDto("目录不存在")); }
    }

    /// <summary>列出子目录(用于前端目录浏览选择)</summary>
    [HttpGet("dirs")]
    public ActionResult<List<DirInfoDto>> Dirs([FromQuery] string? dir)
    {
        try { return Ok(scanService.ListDirectories(dir)); }
        catch (UnauthorizedAccessException ex) { return Problem(ex.Message, statusCode: 403); }
    }
}