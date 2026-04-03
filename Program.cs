using System.Threading.RateLimiting;

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://+:{port}");

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddCors(opt => opt.AddDefaultPolicy(policy =>
    policy.WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? [])
          .AllowAnyHeader()
          .AllowAnyMethod()
          .AllowCredentials()));

builder.Services.AddRateLimiter(opt =>
{
    opt.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 5
            }));

    // Limite mais restrito para login
    opt.AddPolicy("auth", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(15)
            }));

    opt.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await ctx.HttpContext.Response.WriteAsync("Too many requests. Try again later.", ct);
    };
});

var app = builder.Build();

app.UseCors();

// Short-circuit OPTIONS preflights here — YARP forwards them to the API by default,
// causing 502 when the API is unreachable. CORS headers were already set above.
app.Use(async (ctx, next) =>
{
    if (HttpMethods.IsOptions(ctx.Request.Method))
    {
        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        return;
    }
    await next(ctx);
});

app.UseRateLimiter();
app.MapReverseProxy();

app.Run();
