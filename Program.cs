using HonestLicenseServer.Data;
using HonestLicenseServer.Authentication;
using HonestLicenseServer.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
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
        await ApiProblems.WriteAsync(context, StatusCodes.Status500InternalServerError,
            "internal_server_error", "An unexpected server error occurred.",
            builder.Environment.IsDevelopment() ? error?.Message : null);
    });
builder.Services.AddControllers().ConfigureApiBehaviorOptions(options =>
{
    options.InvalidModelStateResponseFactory = actionContext =>
    {
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
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "opaque",
        Description = "Access token returned by POST /api/auth/login or /api/auth/refresh."
    });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = []
    });
});
builder.Services.AddDbContext<HonestDbContext>(options =>
    options.UseSqlite(connectionStringBuilder.ConnectionString));
builder.Services.AddSingleton<LoginAttemptLimiter>();
builder.Services.AddSingleton<LicenseSignatureVerifier>();
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
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
    await DatabaseSchema.EnsureCurrentAsync(db);
    if (!db.Database.CanConnect())
        throw new InvalidOperationException($"Не удалось открыть базу данных: {connectionStringBuilder.DataSource}");
}

app.UseExceptionHandler();
app.UseRateLimiter();
app.UseAuthentication();
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
