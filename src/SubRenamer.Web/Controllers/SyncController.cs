using Microsoft.AspNetCore.Mvc;
using SubRenamer.Web.Models;
using SubRenamer.Web.Services;

namespace SubRenamer.Web.Controllers;

[ApiController]
[Route("api/sync")]
public class SyncController(SubSyncService syncService) : ControllerBase
{
    /// <summary>创建安全调轴任务，输出仅写入 staging</summary>
    [HttpPost]
    [HttpPost("tasks")]
    public ActionResult<SyncTaskCreatedDto> Create([FromBody] SyncTaskRequestDto request)
    {
        if (request.Items is null || request.Items.Count == 0)
            return BadRequest(new ErrorResponseDto("未提供调轴项"));

        try
        {
            return Accepted(syncService.CreateTask(request));
        }
        catch (SyncQueueFullException ex)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new ErrorResponseDto(ex.Message));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ErrorResponseDto(ex.Message));
        }
    }

    /// <summary>查询调轴任务进度</summary>
    [HttpGet("{taskId}/status")]
    [HttpGet("tasks/{taskId}")]
    public ActionResult<SyncTaskDto> Status(string taskId)
    {
        var task = syncService.GetTask(taskId);
        return task == null ? NotFound(new ErrorResponseDto("任务不存在")) : Ok(task);
    }

    /// <summary>取消排队中或执行中的调轴任务</summary>
    [HttpPost("{taskId}/cancel")]
    [HttpPost("tasks/{taskId}/cancel")]
    public ActionResult<SyncTaskDto> Cancel(string taskId)
    {
        var task = syncService.CancelTask(taskId);
        return task == null ? NotFound(new ErrorResponseDto("任务不存在")) : Accepted(task);
    }
}
