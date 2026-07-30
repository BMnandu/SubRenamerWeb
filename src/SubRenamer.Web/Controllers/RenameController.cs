using Microsoft.AspNetCore.Mvc;
using SubRenamer.Web.Models;
using SubRenamer.Web.Services;

namespace SubRenamer.Web.Controllers;

[ApiController]
[Route("api/rename")]
public class RenameController(RenameService renameService) : ControllerBase
{
    /// <summary>执行改名:字幕重命名后写入视频所在目录(混合模式拷贝/同文件夹改名)</summary>
    [HttpPost]
    public RenameResponseDto Rename([FromBody] RenameRequestDto req)
        => renameService.Rename(req);
}