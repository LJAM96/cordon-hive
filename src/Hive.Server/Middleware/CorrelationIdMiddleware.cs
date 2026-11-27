using Hive.Contracts;

namespace Hive.Server.Middleware;

/// <summary>
/// Propagates or assigns an X-Correlation-Id header on every HTTP request and response.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    private const int MaxCorrelationIdLength = 128;
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context.Request);
        context.TraceIdentifier = correlationId;
        context.Items[ProtocolConstants.CorrelationIdHeader] = correlationId;
        context.Response.Headers[ProtocolConstants.CorrelationIdHeader] = correlationId;

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[ProtocolConstants.CorrelationIdHeader] = correlationId;
            return Task.CompletedTask;
        });

        await _next(context);
    }

    public static string GetCorrelationId(HttpContext context)
    {
        if (context.Items.TryGetValue(ProtocolConstants.CorrelationIdHeader, out var value) &&
            value is string correlationId &&
            !string.IsNullOrWhiteSpace(correlationId))
        {
            return correlationId;
        }

        return context.TraceIdentifier;
    }

    private static string ResolveCorrelationId(HttpRequest request)
    {
        if (request.Headers.TryGetValue(ProtocolConstants.CorrelationIdHeader, out var values))
        {
            var candidate = values.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(candidate) && candidate.Length <= MaxCorrelationIdLength)
            {
                return candidate;
            }
        }

        return Guid.NewGuid().ToString("N");
    }
}
