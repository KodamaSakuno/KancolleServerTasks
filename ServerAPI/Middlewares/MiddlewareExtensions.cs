namespace ServerAPI.Middlewares;

public static class MiddlewareExtensions
{
    public static IApplicationBuilder UseDatabaseTransaction(this IApplicationBuilder app) =>
        app.UseMiddleware<DatabaseTransactionMiddleware>();
}
