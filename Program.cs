using HonestLicenseServer.Data;
using HonestLicenseServer.Contracts;
using HonestLicenseServer.Authentication;
using HonestLicenseServer.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using System.Net;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Не задана строка подключения DefaultConnection");
var connectionStringBuilder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
if (!Path.IsPathRooted(connectionStringBuilder.DataSource))
    connectionStringBuilder.DataSource = Path.GetFullPath(connectionStringBuilder.DataSource, AppContext.BaseDirectory);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler(options =>
    options.ExceptionHandler = async context =>
    {
        var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("UnhandledException")
            .LogError(error, "Unhandled exception while processing {Method} {Path}",
                context.Request.Method, context.Request.Path);
        if (context.Request.Path.Equals("/api/connection-requests"))
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new ConnectionRequestErrorResponse(
                false, "Не удалось отправить заявку. Попробуйте позже."));
            return;
        }
        await ApiProblems.WriteAsync(context, StatusCodes.Status500InternalServerError,
            "internal_server_error", "An unexpected server error occurred.",
            builder.Environment.IsDevelopment() ? error?.Message : null);
    });
builder.Services.AddControllers(options => options.Filters.Add<ApiErrorResultFilter>())
    .ConfigureApiBehaviorOptions(options =>
{
    options.InvalidModelStateResponseFactory = actionContext =>
    {
        if (actionContext.HttpContext.Request.Path.Equals("/api/connection-requests"))
            return new BadRequestObjectResult(new ConnectionRequestErrorResponse(
                false, "Проверьте заполненные данные."));

        var result = ApiProblems.Create(actionContext.HttpContext, StatusCodes.Status400BadRequest,
            "validation_failed", "Request validation failed.");
        if (result.Value is Microsoft.AspNetCore.Mvc.ProblemDetails problem)
        {
            problem.Extensions["errors"] = actionContext.ModelState
                .Where(x => x.Value?.Errors.Count > 0)
                .ToDictionary(x => x.Key, x => x.Value!.Errors
                    .Select(error => string.IsNullOrWhiteSpace(error.ErrorMessage)
                        ? "The supplied value is invalid."
                        : error.ErrorMessage).ToArray());
        }
        return result;
    };
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.CustomOperationIds(api =>
    {
        var controller = api.ActionDescriptor.RouteValues["controller"];
        var action = api.ActionDescriptor.RouteValues["action"];
        return $"{controller}_{action}";
    });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "opaque",
        Description = "Access token returned by POST /api/auth/login or /api/auth/refresh."
    });
    options.AddSecurityDefinition("AdminKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        Name = "X-Admin-Key",
        In = ParameterLocation.Header,
        Description = "Administrative API key used by HonestDesk."
    });
    options.OperationFilter<OpenApiSecurityOperationFilter>();
});
builder.Services.AddDbContext<HonestDbContext>(options =>
    options.UseSqlite(connectionStringBuilder.ConnectionString));
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));
builder.Services.AddTransient<IConnectionRequestNotifier, EmailConnectionRequestNotifier>();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
});
builder.Services.AddSingleton<LoginAttemptLimiter>();
builder.Services.AddSingleton<LicenseSignatureVerifier>();
builder.Services.AddHttpClient<IYandexPublicDownloadResolver, YandexPublicDownloadResolver>(client =>
    client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddAuthentication(OpaqueBearerDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, OpaqueBearerHandler>(
        OpaqueBearerDefaults.Scheme, _ => { });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(OpaqueBearerDefaults.ActiveClientPolicy, policy =>
        policy.RequireAuthenticatedUser()
            .RequireClaim(HonestClaimTypes.ClientActive, bool.TrueString));
    options.AddPolicy(OpaqueBearerDefaults.ActiveDevicePolicy, policy =>
        policy.RequireAuthenticatedUser()
            .RequireClaim(HonestClaimTypes.ClientActive, bool.TrueString)
            .RequireClaim(HonestClaimTypes.DeviceStatus, "Active"));
});
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, HonestAuthorizationResultHandler>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) => new ValueTask(ApiProblems.WriteAsync(
        context.HttpContext, StatusCodes.Status429TooManyRequests,
        "rate_limit_exceeded", "Too many requests", "Try again later."));
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.AddPolicy("refresh", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.AddPolicy("support", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst(HonestClaimTypes.ClientId)?.Value ??
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromHours(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.AddPolicy("connection-requests", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(10),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
    await db.Database.EnsureCreatedAsync();
    await DatabaseSchema.EnsureCurrentAsync(db);
    if (!db.Database.CanConnect())
        throw new InvalidOperationException($"Не удалось открыть базу данных: {connectionStringBuilder.DataSource}");
}

app.UseExceptionHandler();
app.UseForwardedHeaders();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Honest License API v1");
    options.DocumentTitle = "Honest License API";
});

app.MapControllers();
app.MapGet("/", () => Results.Redirect("/swagger"))
    .ExcludeFromDescription();

app.Run();

public partial class Program;
