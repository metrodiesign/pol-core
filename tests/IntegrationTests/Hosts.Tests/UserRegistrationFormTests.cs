extern alias ApiHost;
using ApiHost::Api.Merchants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Hosts.Tests;

/// <summary>
/// Merchant frontend contract: producerCode is the canonical multipart field and maps to domain SaleCode.
/// </summary>
public sealed class UserRegistrationFormTests
{
    private static IFormCollection Form(params (string Key, string Value)[] fields) =>
        new FormCollection(fields.ToDictionary(f => f.Key, f => new StringValues(f.Value)));

    [Fact]
    public void The_producer_code_field_maps_to_domain_sale_code()
    {
        var form = UserRegistrationForm.From(
            Form(("firstName", "Somchai"), ("lastName", "Jaidee"), ("personType", "Individual"), ("producerCode", "77001")));

        Assert.Equal("77001", form.SaleCode);
    }

    [Fact]
    public void The_legacy_saleCode_key_binds_nothing()
    {
        var form = UserRegistrationForm.From(
            Form(("firstName", "Somchai"), ("lastName", "Jaidee"), ("personType", "Individual"), ("saleCode", "77001")));

        Assert.Null(form.SaleCode); // not accepted under the old name (REQ-10.3) — and no other field takes it
    }

    [Fact]
    public void Missing_person_type_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => UserRegistrationForm.From(
            Form(("firstName", "Somchai"), ("lastName", "Jaidee"))));
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("0")]
    [InlineData("3")]
    public void Unsupported_person_type_is_rejected(string value)
    {
        Assert.Throws<ArgumentException>(() => UserRegistrationForm.From(
            Form(("firstName", "Somchai"), ("lastName", "Jaidee"), ("personType", value))));
    }

    [Theory]
    [InlineData("Individual", 1)]
    [InlineData("Juristic", 2)]
    public void Valid_person_type_binds_one_based_value(string value, int expected)
    {
        var form = UserRegistrationForm.From(
            Form(("firstName", "Somchai"), ("lastName", "Jaidee"), ("personType", value)));

        Assert.Equal(expected, (int)form.IdentityType);
    }
}
