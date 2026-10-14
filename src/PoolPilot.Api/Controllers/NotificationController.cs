using Microsoft.AspNetCore.Mvc;

using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;

namespace PoolPilot.Api.Controllers;

/// <summary>
/// 通知：通道（提供商）/ 样板 / 投递记录。
///
/// 三块放在一个控制器里，是因为它们描述同一件事的三个面：**谁能发、发什么、发了什么**。
/// 拆成三个控制器只会让人在三个文件之间来回找——它们没有独立的生命周期。
///
/// 本控制器**不标 AllowAnonymous**：通知配置里有 SMTP 口令，它必须跟着全局策略
/// 一起被登录挡住（Program.cs 里 MapControllers().RequireAuthorization()）。
/// </summary>
[ApiController]
[Route("api/notifications")]
public sealed class NotificationController(
    ListNotificationProvidersUseCase listProviders,
    CreateNotificationProviderUseCase createProvider,
    UpdateNotificationProviderUseCase updateProvider,
    DeleteNotificationProviderUseCase deleteProvider,
    SendTestNotificationUseCase sendTest,
    ListNotificationTemplatesUseCase listTemplates,
    UpdateNotificationTemplateUseCase updateTemplate,
    ListNotificationLogsUseCase listLogs) : ControllerBase
{
    [HttpGet("providers")]
    public async Task<ActionResult<IReadOnlyList<NotificationProviderDto>>> ListProviders(
        CancellationToken cancellationToken = default) =>
        Ok(await listProviders.ExecuteAsync(cancellationToken).ConfigureAwait(false));

    [HttpPost("providers")]
    public async Task<ActionResult<NotificationProviderDto>> CreateProvider(
        NotificationProviderUpsertDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        NotificationProviderDto created = await createProvider
            .ExecuteAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return CreatedAtAction(nameof(ListProviders), created);
    }

    [HttpPut("providers/{id:long}")]
    public async Task<ActionResult<NotificationProviderDto>> UpdateProvider(
        long id,
        NotificationProviderUpsertDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await updateProvider.ExecuteAsync(id, request, cancellationToken).ConfigureAwait(false));
    }

    [HttpDelete("providers/{id:long}")]
    public async Task<IActionResult> DeleteProvider(long id, CancellationToken cancellationToken = default)
    {
        await deleteProvider.ExecuteAsync(id, cancellationToken).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>用这条通道发一封测试邮件。发失败**不是 5xx**：结果里带着 SMTP 的原话。</summary>
    [HttpPost("providers/{id:long}/test")]
    public async Task<ActionResult<NotificationDeliveryDto>> TestProvider(
        long id,
        CancellationToken cancellationToken = default) =>
        Ok(await sendTest.ExecuteAsync(id, cancellationToken).ConfigureAwait(false));

    [HttpGet("templates")]
    public async Task<ActionResult<IReadOnlyList<NotificationTemplateDto>>> ListTemplates(
        CancellationToken cancellationToken = default) =>
        Ok(await listTemplates.ExecuteAsync(cancellationToken).ConfigureAwait(false));

    [HttpPut("templates/{purpose}")]
    public async Task<ActionResult<NotificationTemplateDto>> UpdateTemplate(
        string purpose,
        NotificationTemplateUpsertDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await updateTemplate.ExecuteAsync(purpose, request, cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("logs")]
    public async Task<ActionResult<IReadOnlyList<NotificationLogDto>>> ListLogs(
        [FromQuery] string? purpose = null,
        CancellationToken cancellationToken = default) =>
        Ok(await listLogs.ExecuteAsync(purpose, cancellationToken).ConfigureAwait(false));
}
