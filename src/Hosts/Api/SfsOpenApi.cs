using Microsoft.OpenApi;

namespace Api;

/// <summary>
/// Metadata marker for an endpoint whose SFS query parameters (<c>page</c>, <c>limit</c>, <c>filters</c>,
/// <c>sort</c>, <c>search</c>) are read from the raw query string via <see cref="SfsQueryParser"/>. Minimal APIs
/// emit no OpenAPI parameters for raw-query reads, so the OpenAPI operation transformer (Program.cs
/// <c>AddOperationTransformer</c>) adds their declarations wherever it finds this marker. (REQ-13)
/// </summary>
internal sealed class SfsQueryParamsMarker;

internal static class SfsOpenApi
{
    /// <summary>Declares the five SFS query parameters on an operation so they appear in the OpenAPI document
    /// (and therefore in Scalar) even though they are bound from <c>HttpContext.Request.Query</c>.</summary>
    public static void AddQueryParameters(OpenApiOperation operation)
    {
        operation.Parameters ??= [];
        operation.Parameters.Add(Param("page", JsonSchemaType.Integer, "เลขหน้าแบบเริ่มที่ 1 (ค่าเริ่มต้น 1; clamp ไม่ให้ต่ำกว่า 1)"));
        operation.Parameters.Add(Param("limit", JsonSchemaType.Integer, "จำนวนรายการต่อหน้า (ค่าเริ่มต้น 25; clamp ในช่วง 1 ถึง 100)"));
        operation.Parameters.Add(Param("filters", JsonSchemaType.String,
            "JSON array ของเงื่อนไข filter แบบ URL-encoded: [{\"field\",\"operator\",\"value\"|\"values\"}]"));
        operation.Parameters.Add(Param("sort", JsonSchemaType.String,
            "JSON array ของเงื่อนไข sort แบบ URL-encoded: [{\"field\",\"order\":\"ASC\"|\"DESC\"}]"));
        operation.Parameters.Add(Param("search", JsonSchemaType.String,
            "JSON object สำหรับค้นหาแบบ URL-encoded: {\"query\",\"fields\":[...]}"));
    }

    private static OpenApiParameter Param(string name, JsonSchemaType type, string description) => new()
    {
        Name = name,
        In = ParameterLocation.Query,
        Required = false,
        Description = description,
        Schema = new OpenApiSchema { Type = type },
    };
}
