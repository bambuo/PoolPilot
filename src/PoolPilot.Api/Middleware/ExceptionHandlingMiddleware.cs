using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using PoolPilot.Domain.Exceptions;
using PoolPilot.Infrastructure.Lighter;

namespace PoolPilot.Api.Middleware;

/// <summary>
/// 统一异常映射。
///
/// 分层约定（docs/CODING_STANDARDS.md 6）：
/// 领域异常 → 404 / 400；上游限速 → 429；上游故障 → 502；其余 → 500。
///
/// **上游失败必须显式暴露**：Lighter 的 HTTP 200 与业务成功不等价，
/// 限速（权重超配）会返回 429 或 405。把这类情况映射成空结果会让
/// "上游没数据"与"上游拒绝了我们"混为一谈，而这两者对使用者的含义完全不同。
/// </summary>
public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (InvalidLighterFieldException ex)
        {
            logger.LogError(ex, "上游字段口径不符：{Field}", ex.FieldName);
            await WriteAsync(context, StatusCodes.Status502BadGateway, $"{ex.FieldName} 字段口径不符：{ex.Message}").ConfigureAwait(false);
        }
        catch (LighterRateLimitedException ex)
        {
            logger.LogWarning(ex, "上游限速：{Endpoint}", ex.Endpoint);
            await WriteAsync(context, StatusCodes.Status429TooManyRequests, ex.Message).ConfigureAwait(false);
        }
        catch (LighterHttpException ex)
        {
            logger.LogError(ex, "上游请求失败：{Endpoint} HTTP {Status}", ex.Endpoint, ex.StatusCode);
            await WriteAsync(context, StatusCodes.Status502BadGateway, ex.Message).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "参数错误");
            await WriteAsync(context, StatusCodes.Status400BadRequest, ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "未预期异常");
            await WriteAsync(context, StatusCodes.Status500InternalServerError, "服务内部错误").ConfigureAwait(false);
        }
    }

    private static Task WriteAsync(HttpContext context, int statusCode, string message)
    {
        if (context.Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";

        return context.Response.WriteAsJsonAsync(new { error = message });
    }
}
