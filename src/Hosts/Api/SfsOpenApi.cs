using Microsoft.OpenApi;

namespace Api;

/// <summary>
/// Metadata marker for an endpoint whose SFS query parameters (<c>page</c>, <c>limit</c>, <c>filters</c>,
/// <c>sort</c>, <c>search</c>) are read from the raw query string via <see cref="SfsQueryParser"/>. Minimal APIs
/// emit no OpenAPI parameters for raw-query reads, so the OpenAPI operation transformer (Program.cs
/// <c>AddOperationTransformer</c>) adds their declarations wherever it finds this marker. (REQ-13)
/// </summary>
internal sealed class SfsQueryParamsMarker;

/// <summary>
/// Metadata marker for an endpoint that reads only <c>page</c>/<c>limit</c> from the raw query string plus its
/// own typed <c>productFilters</c> object — the products list, whose §2 input contract has no
/// filter/sort/search concept, so advertising those three would document a surface that does nothing (REQ-7.4).
/// </summary>
internal sealed class ProductQueryParamsMarker;

internal static class SfsOpenApi
{
    /// <summary>Declares the five SFS query parameters on an operation so they appear in the OpenAPI document
    /// (and therefore in Scalar) even though they are bound from <c>HttpContext.Request.Query</c>.</summary>
    public static void AddQueryParameters(OpenApiOperation operation)
    {
        var parameters = AddPagingParameters(operation);
        parameters.Add(Param("filters", JsonSchemaType.String,
            "JSON array ของเงื่อนไข filter แบบ URL-encoded: [{\"field\",\"operator\",\"value\"|\"values\"}]"));
        parameters.Add(Param("sort", JsonSchemaType.String,
            "JSON array ของเงื่อนไข sort แบบ URL-encoded: [{\"field\",\"order\":\"ASC\"|\"DESC\"}]"));
        parameters.Add(Param("search", JsonSchemaType.String,
            "JSON object สำหรับค้นหาแบบ URL-encoded: {\"query\",\"fields\":[...]}"));
    }

    /// <summary>Declares the products list surface: paging plus the mandatory typed <c>productFilters</c>
    /// object, and deliberately none of the three SFS parameters (REQ-7.4).</summary>
    public static void AddProductQueryParameters(OpenApiOperation operation)
    {
        AddPagingParameters(operation).Add(Param("productFilters", JsonSchemaType.String,
            "JSON object ของตัวกรองเอกสารแบบ URL-encoded (บังคับ — ต้องระบุ productGroup หรือ insuranceType อย่างน้อยหนึ่ง"
            + " เพื่อเลือกฝั่ง Motor/NonMotor; รหัสผู้ขายกำหนดโดย server เองจากผู้ใช้ที่ยืนยันตัวตนแล้ว ไม่ใช่ member ที่นี่): {\"searchText\",\"insuredName\","
            + "\"policyNo\",\"applicationNo\",\"documentType\",\"productGroup\",\"paymentStatus\":\"UNPAID\"|\"PAID\"|\"ALL\","
            + "\"insuranceType\":\"Motor\"|\"NonMotor\" (บังคับเมื่อไม่ส่ง productGroup และต้องไม่ขัดแย้งกับ productGroup),"
            + "\"countMode\":\"EXACT\"|\"FAST\" (ค่าเริ่มต้น EXACT; FAST ให้ totalRows/totalPages เป็น null),"
            + "\"coverageStartFrom\",\"coverageStartTo\",\"coverageEndFrom\",\"coverageEndTo\",\"paidDateFrom\",\"paidDateTo\"}",
            required: true));
    }

    private static IList<IOpenApiParameter> AddPagingParameters(OpenApiOperation operation)
    {
        var parameters = operation.Parameters ??= [];
        parameters.Add(Param("page", JsonSchemaType.Integer, "เลขหน้าแบบเริ่มที่ 1 (ค่าเริ่มต้น 1; clamp ไม่ให้ต่ำกว่า 1)"));
        parameters.Add(Param("limit", JsonSchemaType.Integer, "จำนวนรายการต่อหน้า (ค่าเริ่มต้น 25; clamp ในช่วง 1 ถึง 25)"));
        return parameters;
    }

    private static OpenApiParameter Param(string name, JsonSchemaType type, string description,
        bool required = false) => new()
    {
        Name = name,
        In = ParameterLocation.Query,
        Required = required,
        Description = description,
        Schema = new OpenApiSchema { Type = type },
    };
}
