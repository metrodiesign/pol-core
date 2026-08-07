using System.Text.Json;
using SharedKernel;

namespace SharedKernel.Tests;

public sealed class CommerceItemMetadataTests
{
    [Fact]
    public void Codec_roundtrips_canonical_server_owned_insurance_facts()
    {
        var metadata = new CommerceItemMetadata(
            CommerceItemMetadataCodec.InsuranceDocumentSource,
            "POLICY", "P-2569-1", new DateOnly(2026, 8, 1), new DateOnly(2027, 7, 31));

        var json = CommerceItemMetadataCodec.Serialize(metadata);
        var decoded = CommerceItemMetadataCodec.Parse(json);

        Assert.Equal(metadata, decoded);
        Assert.Equal(
            "{\"sourceType\":\"insurance_document\",\"documentType\":\"POLICY\",\"policyNumber\":\"P-2569-1\",\"startDate\":\"2026-08-01\",\"endDate\":\"2027-07-31\"}",
            json);
    }

    [Theory]
    [InlineData("{\"sourceType\":\"unknown\"}")]
    [InlineData("{\"sourceType\":\"insurance_document\",\"insuredName\":\"PII\"}")]
    [InlineData("{\"sourceType\":\"insurance_document\",\"secret\":\"value\"}")]
    [InlineData("not-json")]
    public void Codec_rejects_unknown_source_unknown_fields_and_invalid_json(string json) =>
        Assert.ThrowsAny<Exception>(() => CommerceItemMetadataCodec.Parse(json));

    [Fact]
    public void Detached_projection_fails_loud_on_invalid_persisted_json() =>
        Assert.Throws<JsonException>(() => CommerceItemMetadataCodec.ToJsonElement("{"));
}
