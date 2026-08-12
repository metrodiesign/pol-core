using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Api;

internal sealed record AudienceRequestBodyMarker(Type Merchant, Type Admin);
internal sealed record AudienceResponseMarker(string Status, Type Merchant, Type Admin);

internal static class AudienceOpenApi
{
    public static async Task ApplyAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        AudienceRequestBodyMarker marker,
        CancellationToken cancellationToken)
    {
        if (operation.RequestBody?.Content is null)
            return;
        var parameter = context.Description.ParameterDescriptions
            .FirstOrDefault(x => x.Source == Microsoft.AspNetCore.Mvc.ModelBinding.BindingSource.Body);
        var schema = await SchemaAsync(
            context, marker.Merchant, marker.Admin, parameter, cancellationToken);
        foreach (var media in operation.RequestBody.Content.Values)
            media.Schema = schema;
    }

    public static async Task ApplyAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        AudienceResponseMarker marker,
        CancellationToken cancellationToken)
    {
        if (operation.Responses?.TryGetValue(marker.Status, out var response) != true
            || response is not OpenApiResponse concrete
            || concrete.Content is null)
            return;
        var schema = await SchemaAsync(
            context, marker.Merchant, marker.Admin, parameter: null, cancellationToken);
        foreach (var media in concrete.Content.Values)
            media.Schema = schema;
    }

    private static async Task<IOpenApiSchema> SchemaAsync(
        OpenApiOperationTransformerContext context,
        Type merchantType,
        Type adminType,
        Microsoft.AspNetCore.Mvc.ApiExplorer.ApiParameterDescription? parameter,
        CancellationToken cancellationToken)
    {
        if (context.DocumentName == OpenApiDocuments.Merchant)
            return await context.GetOrCreateSchemaAsync(merchantType, parameter!, cancellationToken);
        if (context.DocumentName == OpenApiDocuments.Admin)
            return await context.GetOrCreateSchemaAsync(adminType, parameter!, cancellationToken);

        var merchant = await context.GetOrCreateSchemaAsync(merchantType, parameter!, cancellationToken);
        var admin = await context.GetOrCreateSchemaAsync(adminType, parameter!, cancellationToken);
        return new OpenApiSchema { OneOf = [merchant, admin] };
    }
}
