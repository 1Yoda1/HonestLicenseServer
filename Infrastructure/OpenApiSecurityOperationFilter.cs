using HonestLicenseServer.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace HonestLicenseServer.Infrastructure;

public sealed class OpenApiSecurityOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        operation.Security = [];
        var descriptor = context.ApiDescription.ActionDescriptor;
        if (descriptor is ControllerActionDescriptor controller && controller.ControllerName == "Admin")
        {
            operation.Security.Add(Requirement("AdminKey"));
            return;
        }

        var metadata = descriptor.EndpointMetadata;
        if (metadata.OfType<IAllowAnonymous>().Any()) return;
        IAuthorizeData[] authorization = metadata.OfType<IAuthorizeData>().ToArray();
        if (authorization.Any(item =>
                (item.AuthenticationSchemes ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(ServiceInstallOnlyDefaults.Scheme, StringComparer.Ordinal)))
        {
            operation.Security.Add(Requirement("InstallBearer"));
            return;
        }
        if (authorization.Length > 0) operation.Security.Add(Requirement("Bearer"));
    }

    private static OpenApiSecurityRequirement Requirement(string scheme)
    {
        var document = new OpenApiDocument
        {
            Components = new OpenApiComponents
            {
                SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
                {
                    [scheme] = new OpenApiSecurityScheme()
                }
            }
        };
        return new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(scheme, document)] = []
        };
    }
}
