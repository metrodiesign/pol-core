extern alias ApiHost;
using ApiHost::Api.Merchants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Hosts.Tests;

/// <summary>
/// products-external-source-of-truth REQ-10.3/10.7 — the registration form's sale-code field is spelled
/// <c>saleCode</c> on the wire, and the old <c>producerCode</c> spelling is gone rather than quietly tolerated.
/// <para>This is the one rename in the feature that fails INVISIBLY (design F24): the field is optional, so a
/// form still posting the old key would get a 201 back while the value silently landed as null — and only
/// resurface much later as a 403 from the catalogue, far from the mapping that dropped it. The repo's rename
/// gate cannot see it either (<c>check_rename_identifiers.py</c> strips string literals before matching), so
/// the wire name is pinned here instead, in both directions.</para>
/// </summary>
public sealed class UserRegistrationFormTests
{
    private static IFormCollection Form(params (string Key, string Value)[] fields) =>
        new FormCollection(fields.ToDictionary(f => f.Key, f => new StringValues(f.Value)));

    [Fact]
    public void The_sale_code_field_binds_from_the_saleCode_key()
    {
        var form = UserRegistrationForm.From(
            Form(("firstName", "Somchai"), ("lastName", "Jaidee"), ("saleCode", "77001")));

        Assert.Equal("77001", form.SaleCode);
    }

    [Fact]
    public void The_retired_producerCode_key_binds_nothing()
    {
        var form = UserRegistrationForm.From(
            Form(("firstName", "Somchai"), ("lastName", "Jaidee"), ("producerCode", "77001")));

        Assert.Null(form.SaleCode); // not accepted under the old name (REQ-10.3) — and no other field takes it
    }
}
