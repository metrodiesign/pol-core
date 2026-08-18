using System.Text.Json.Nodes;
using Api.Iam;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.OpenApi;

namespace Api;

internal static class OpenApiDocuments
{
    public const string Combined = "v1";
    public const string Merchant = "merchant";
    public const string Admin = "admin";
    public const string Integration = "integration";

    public static readonly IReadOnlyList<string> All = [Combined, Merchant, Admin, Integration];

    private static readonly (string Name, string[] Tags)[] Groups =
    [
        ("ผลิตภัณฑ์", ["ผลิตภัณฑ์"]),
        ("ตะกร้าสินค้า", ["ตะกร้าสินค้า"]),
        ("คำสั่งซื้อ", ["คำสั่งซื้อ"]),
        ("การชำระเงิน", ["การชำระเงิน", "Payment capability", "Webhooks", "การเชื่อมต่อ PSP", "กฎเส้นทาง PSP"]),
        ("ร้านค้า", ["ร้านค้า (ผู้ดูแลระบบ)", "การเข้าสู่ระบบ (ผู้ใช้ร้านค้า)", "ผู้ใช้ร้านค้า",
            "ผู้ใช้ร้านค้า (ผู้ดูแลระบบ)", "แหล่งที่มารายการ"]),
        ("ผู้ดูแลระบบ", ["ผู้ดูแลระบบ", "การเข้าสู่ระบบ"]),
        ("Iam", ["บทบาท (ผู้ดูแลระบบ)", "บทบาท (ผู้ใช้ร้านค้า)",
            "บทบาทผู้ใช้ร้านค้า (ผู้ดูแลระบบ)", "ไคลเอนต์ API"]),
        ("Governance", ["การอนุมัติ", "บันทึกการตรวจสอบ"]),
        ("Notifications", ["การแจ้งเตือน", "เว็บฮุก"]),
        ("Reporting", ["รายงาน", "ธุรกรรม"]),
        ("แผนก", ["แผนก"]),
        ("ระดับ", ["ระดับ"]),
        ("สำนักงาน", ["สำนักงาน"]),
        ("ตำแหน่ง", ["ตำแหน่ง"]),
    ];

    public static bool ShouldInclude(string documentName, ApiDescription description)
    {
        if (documentName == Combined)
            return true;

        var schemes = AuthPolicyScheme.SecuritySchemeIdsFor(description.ActionDescriptor.EndpointMetadata);
        var path = "/" + (description.RelativePath ?? string.Empty).TrimStart('/');
        var publicCustomerPayment = path.StartsWith("/api/v1/orders/{token}/", StringComparison.Ordinal);

        return documentName switch
        {
            Merchant => schemes.Contains("MerchantUserSession", StringComparer.Ordinal)
                || path.StartsWith("/api/v1/merchants/auth/", StringComparison.Ordinal)
                || path == "/api/v1/merchants/users/register"
                || publicCustomerPayment,
            Admin => schemes.Contains("AdminSession", StringComparer.Ordinal)
                || path.StartsWith("/api/v1/admins/auth/", StringComparison.Ordinal),
            Integration => publicCustomerPayment
                || path.StartsWith("/api/v1/webhooks/{pspConnectionId", StringComparison.Ordinal),
            _ => false,
        };
    }

    public static string Title(string documentName) => documentName switch
    {
        Merchant => "pol-core Merchant API",
        Admin => "pol-core Admin API",
        Integration => "pol-core Integration API",
        _ => "pol-core API",
    };

    public static string Description(string documentName) => documentName switch
    {
        Merchant => "สัญญา API สำหรับ Merchant Console และหน้าชำระเงินของลูกค้าผ่าน public capability link",
        Admin => "สัญญา API สำหรับ Admin Console และงานควบคุมระบบภายใน Admin scope",
        Integration => "สัญญา API สำหรับหน้าชำระเงินของลูกค้าและ inbound PSP webhook callback",
        _ => "สัญญา API รวมสำหรับ Merchant Console, MerchantUser BFF, Admin Console, หน้าชำระเงินของลูกค้า และ PSP integration",
    };

    public static bool IncludesSecurityScheme(string documentName, string schemeId) =>
        documentName == Combined
        || documentName == Admin && schemeId == "AdminSession"
        || documentName == Merchant && schemeId == "MerchantUserSession";

    public static IReadOnlyList<string> SecuritySchemeIds(
        string documentName, IReadOnlyList<string> schemeIds) =>
        [.. schemeIds.Where(id => IncludesSecurityScheme(documentName, id))];

    public static JsonNodeExtension CreateTagGroups(OpenApiDocument document)
    {
        var activeTags = new HashSet<string>(StringComparer.Ordinal);
        if (document.Tags is not null)
            foreach (var tag in document.Tags)
                if (tag.Name is { Length: > 0 } name)
                    activeTags.Add(name);

        var knownTags = Groups.SelectMany(group => group.Tags).ToHashSet(StringComparer.Ordinal);
        var unknownTags = activeTags.Except(knownTags).Order(StringComparer.Ordinal).ToArray();
        if (unknownTags.Length > 0)
            throw new InvalidOperationException("OpenAPI tags without a module group: " + string.Join(", ", unknownTags));

        var result = new JsonArray();
        foreach (var group in Groups)
        {
            var tags = new JsonArray();
            foreach (var tag in group.Tags.Where(activeTags.Contains))
                tags.Add(tag);
            if (tags.Count > 0)
                result.Add(new JsonObject { ["name"] = group.Name, ["tags"] = tags });
        }

        return new JsonNodeExtension(result);
    }
}
