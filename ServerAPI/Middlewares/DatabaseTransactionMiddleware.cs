using Microsoft.AspNetCore.Http.Features;
using ServerAPI.Services;

namespace ServerAPI.Middlewares;

public sealed class DatabaseTransactionMiddleware
{
    private readonly RequestDelegate _next;

    public DatabaseTransactionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task Invoke(HttpContext httpContext, DatabaseService databaseService)
    {
        if (httpContext.Features.Get<IEndpointFeature>()?.Endpoint?.Metadata.GetMetadata<TransactionAttribute>() is null)
            return _next(httpContext);

        return InvokeWithTransaction(httpContext, databaseService);
    }
    private async Task InvokeWithTransaction(HttpContext httpContext, DatabaseService databaseService)
    {
        var connection = await databaseService.GetConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await _next(httpContext);

        await transaction.CommitAsync();
    }
}
