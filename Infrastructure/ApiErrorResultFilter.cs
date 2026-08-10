using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace HonestLicenseServer.Infrastructure;

public sealed class ApiErrorResultFilter : IAsyncResultFilter
{
    public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult { Value: not ProblemDetails } result &&
            (result.StatusCode ?? StatusCodes.Status200OK) >= 400)
        {
            var errorProperty = result.Value?.GetType().GetProperty("error");
            if (errorProperty?.GetValue(result.Value) is string code)
            {
                var title = string.Join(' ', code.Split('_'));
                context.Result = ApiProblems.Create(context.HttpContext,
                    result.StatusCode!.Value, code, title);
            }
        }
        return next();
    }
}
