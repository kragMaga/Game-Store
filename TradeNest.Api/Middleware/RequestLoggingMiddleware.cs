using System.Diagnostics;

namespace TradeNest.Api.Middleware;

public sealed class RequestLoggingMiddleware(
    RequestDelegate next,
    ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            await next(context);
        }
        finally
        {
            var elapsedMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            var method = context.Request.Method;
            var path = context.Request.Path.Value ?? "/";
            var statusCode = context.Response.StatusCode;
            var traceId = context.TraceIdentifier;

            if (statusCode >= StatusCodes.Status500InternalServerError)
            {
                logger.LogError(
                    "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMilliseconds:0.0} ms. TraceId: {TraceId}",
                    method,
                    path,
                    statusCode,
                    elapsedMilliseconds,
                    traceId);
            }
            else if (statusCode >= StatusCodes.Status400BadRequest)
            {
                logger.LogWarning(
                    "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMilliseconds:0.0} ms. TraceId: {TraceId}",
                    method,
                    path,
                    statusCode,
                    elapsedMilliseconds,
                    traceId);
            }
            else
            {
                logger.LogInformation(
                    "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMilliseconds:0.0} ms. TraceId: {TraceId}",
                    method,
                    path,
                    statusCode,
                    elapsedMilliseconds,
                    traceId);
            }
        }
    }
}
