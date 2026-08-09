using HonestLicenseServer.Data;
using HonestLicenseServer.Middleware;
using Microsoft.EntityFrameworkCore;

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

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddDbContext<HonestDbContext>(options =>
    options.UseSqlite(connectionStringBuilder.ConnectionString));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HonestDbContext>();
    if (!db.Database.CanConnect())
        throw new InvalidOperationException($"Не удалось открыть базу данных: {connectionStringBuilder.DataSource}");
}

app.UseMiddleware<TestBearerMiddleware>();

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
