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
        if (metadata.OfType<IAuthorizeData>().Any()) operation.Security.Add(Requirement("Bearer"));
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
