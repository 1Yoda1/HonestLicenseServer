using Microsoft.AspNetCore.Mvc;

namespace HonestLicenseServer.Infrastructure;

public static class ApiProblems
{
    public static ObjectResult Create(HttpContext context, int status, string code,
        string title, string? detail = null)
    {
        var problem = new ProblemDetails
        {
            Type = $"https://api.honestflow.ru/problems/{code.Replace('_', '-')}",
            Title = title,
            Status = status,
            Detail = detail,
            Instance = context.Request.Path
        };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = context.TraceIdentifier;
        return new ObjectResult(problem) { StatusCode = status };
    }

    public static Task WriteAsync(HttpContext context, int status, string code,
        string title, string? detail = null)
    {
        var result = Create(context, status, code, title, detail);
        return result.ExecuteResultAsync(new ActionContext { HttpContext = context });
    }
}
