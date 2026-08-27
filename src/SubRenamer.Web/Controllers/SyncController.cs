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

    /// <summary>把通过质量门禁的 staging 候选结果显式提交到媒体目录</summary>
    [HttpPost("{taskId}/commit")]
    [HttpPost("tasks/{taskId}/commit")]
    public async Task<ActionResult<SyncFileOperationResponseDto>> Commit(
        string taskId,
        [FromBody] SyncCommitRequestDto? request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await syncService.CommitTaskAsync(
                taskId,
                request ?? new SyncCommitRequestDto(),
                cancellationToken);
            if (result is null)
                return NotFound(new ErrorResponseDto("任务不存在"));
            return result.Conflicts > 0 ? Conflict(result) : Ok(result);
        }
        catch (SyncTaskNotReadyException ex)
        {
            return Conflict(new ErrorResponseDto(ex.Message));
        }
    }

    /// <summary>回滚本任务已经提交且内容未被外部修改的正式字幕</summary>
    [HttpPost("{taskId}/rollback")]
    [HttpPost("tasks/{taskId}/rollback")]
    public async Task<ActionResult<SyncFileOperationResponseDto>> Rollback(
        string taskId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await syncService.RollbackTaskAsync(taskId, cancellationToken);
            if (result is null)
                return NotFound(new ErrorResponseDto("任务不存在"));
            return result.Conflicts > 0 ? Conflict(result) : Ok(result);
        }
        catch (SyncTaskNotReadyException ex)
        {
            return Conflict(new ErrorResponseDto(ex.Message));
        }
    }
}
