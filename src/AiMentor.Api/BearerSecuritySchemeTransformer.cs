using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AiMentor.Api;

/// <summary>只为启用 JWT 的受保护 v1 操作生成 Bearer OpenAPI 安全声明。</summary>
public sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer, IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "OIDC 提供方签发的 API Access Token"
        };
        return Task.CompletedTask;
    }

    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var requiresAuthorization = context.Description.ActionDescriptor.EndpointMetadata.OfType<IAuthorizeData>().Any();
        if (!requiresAuthorization) return Task.CompletedTask;
        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("Bearer", context.Document, null)] = []
        });
        return Task.CompletedTask;
    }
}
