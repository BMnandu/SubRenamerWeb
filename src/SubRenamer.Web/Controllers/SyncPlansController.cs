using Microsoft.AspNetCore.Mvc;
using SubRenamer.Web.Models;
using SubRenamer.Web.Services;

namespace SubRenamer.Web.Controllers;

[ApiController]
[Route("api/sync/plans")]
public sealed class SyncPlansController(SyncPlanService syncPlanService) : ControllerBase
{
    /// <summary>创建纯预览调轴计划，不写入媒体目录</summary>
    [HttpPost]
    public ActionResult<SyncPlanResponseDto> Create([FromBody] SyncPlanRequestDto request)
    {
        if (request.Items is null || request.Items.Count == 0)
            return BadRequest(new ErrorResponseDto("未提供调轴计划项"));

        return Ok(syncPlanService.CreatePlan(request));
    }
}
