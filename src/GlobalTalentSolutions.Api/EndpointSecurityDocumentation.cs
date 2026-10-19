using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

public sealed class EndpointSecurityDocumentation : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var metadata=context.ApiDescription.ActionDescriptor.EndpointMetadata;
        if(metadata.OfType<IAllowAnonymous>().Any() || !metadata.OfType<IAuthorizeData>().Any()) return;
        operation.Security = [new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme { Reference=new() { Type=ReferenceType.SecurityScheme,Id="Bearer" } }] = []
        }];
    }
}
