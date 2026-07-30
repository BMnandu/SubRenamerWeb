using Microsoft.AspNetCore.Mvc;
using SubRenamer.Web.Models;
using SubRenamer.Web.Services;

namespace SubRenamer.Web.Controllers;

[ApiController]
[Route("api/sync")]
public class SyncController(SubSyncService syncService) : ControllerBase
{
    /// <summary>创建调轴任务(异步),返回 taskId</summary>
    [HttpPost]
    public ActionResult Create([FromBody] SyncRequestDto req)
    {
        if (req.Items == null || req.Items.Count == 0)
            return BadRequest(new ErrorResponseDto("未提供调轴项"));
        var taskId = syncService.CreateTask(req.Items);
        return Ok(new { taskId });
    }

    /// <summary>查询调轴任务进度</summary>
    [HttpGet("{taskId}/status")]
    public ActionResult<SyncTask> Status(string taskId)
    {
        var task = syncService.GetTask(taskId);
        return task == null ? NotFound(new ErrorResponseDto("任务不存在")) : Ok(task);
    }
}
