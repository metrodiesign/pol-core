using System.Text.Json;
using BuildingBlocks.Application;

namespace BuildingBlocks.Tests;

/// <summary>
/// JSON round-trip contract for the SFS value types (REQ-1.3, REQ-1.4, REQ-9.2, REQ-9.3) and the paging
/// envelope math (REQ-2.4, REQ-2.7). The host registers no global string-enum converter, so these assert the
/// enum-annotated converters carry the wire tokens as strings — not integers — and that <c>filters</c> values
/// survive as raw <see cref="JsonElement"/> for apply-time conversion. Serialization uses
/// <see cref="JsonSerializerDefaults.Web"/> to mirror the Hosts-layer parser.
/// </summary>
public sealed class SfsContractsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static TheoryData<FilterOperator, string> OperatorTokens => new()
    {
        { FilterOperator.Equals, "eq" },
        { FilterOperator.NotEquals, "ne" },
        { FilterOperator.GreaterThan, "gt" },
        { FilterOperator.GreaterThanOrEqual, "gte" },
        { FilterOperator.LessThan, "lt" },
        { FilterOperator.LessThanOrEqual, "lte" },
        { FilterOperator.Like, "like" },
        { FilterOperator.ILike, "ilike" },
        { FilterOperator.In, "in" },
        { FilterOperator.NotIn, "not_in" },
        { FilterOperator.IsNull, "is_null" },
        { FilterOperator.IsNotNull, "is_not_null" },
        { FilterOperator.Between, "between" },
        { FilterOperator.Contains, "contains" },
    };

    [Theory]
    [MemberData(nameof(OperatorTokens))]
    public void FilterOperator_round_trips_as_its_wire_token(FilterOperator op, string token)
    {
        var json = JsonSerializer.Serialize(op, Web);

        Assert.Equal($"\"{token}\"", json);                                  // string token, never an integer
        Assert.Equal(op, JsonSerializer.Deserialize<FilterOperator>(json, Web));
    }

    [Theory]
    [InlineData(SortDirection.Asc, "ASC")]
    [InlineData(SortDirection.Desc, "DESC")]
    public void SortDirection_round_trips_as_ASC_or_DESC(SortDirection dir, string token)
    {
        var json = JsonSerializer.Serialize(dir, Web);

        Assert.Equal($"\"{token}\"", json);
        Assert.Equal(dir, JsonSerializer.Deserialize<SortDirection>(json, Web));
    }

    [Fact]
    public void FilterOperator_rejects_a_numeric_token()
    {
        // allowIntegerValues:false -> {"operator":0} is NOT accepted as the ordinal; the token must be the
        // lowercase/snake string (REQ-1.3). A numeric value is malformed -> JsonException (-> 400 at the parser).
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<FilterOperator>("0", Web));
    }

    [Fact]
    public void SortDirection_rejects_a_numeric_token()
    {
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<SortDirection>("0", Web));
    }

    [Fact]
    public void FilterOption_binds_operator_token_and_keeps_values()
    {
        var f = JsonSerializer.Deserialize<FilterOption>(
            """{"field":"code","operator":"not_in","values":["super_admin","support"]}""", Web);

        Assert.NotNull(f);
        Assert.Equal("code", f!.Field);
        Assert.Equal(FilterOperator.NotIn, f.Operator);
        Assert.NotNull(f.Values);
        Assert.Equal(new[] { "super_admin", "support" }, f.Values!.Select(v => v.GetString()));
    }

    [Fact]
    public void FilterOption_keeps_value_as_JsonElement_for_apply_time()
    {
        var f = JsonSerializer.Deserialize<FilterOption>(
            """{"field":"priceAmount","operator":"gte","value":1000}""", Web);

        Assert.Equal(FilterOperator.GreaterThanOrEqual, f!.Operator);
        Assert.NotNull(f.Value);
        Assert.Equal(1000L, f.Value!.Value.GetInt64());                      // raw JSON preserved (REQ-9.3)
    }

    [Fact]
    public void SortOption_binds_order_literal()
    {
        var s = JsonSerializer.Deserialize<SortOption>("""{"field":"name","order":"ASC"}""", Web);

        Assert.Equal("name", s!.Field);
        Assert.Equal(SortDirection.Asc, s.Order);
    }

    [Fact]
    public void SortOption_order_defaults_to_Asc_when_absent()
    {
        var s = JsonSerializer.Deserialize<SortOption>("""{"field":"name"}""", Web);

        Assert.Equal(SortDirection.Asc, s!.Order);
    }

    [Theory]
    [InlineData(5, 25, 1)]      // verify: PagedResult(Total=5, Limit=25).TotalPages == 1
    [InlineData(0, 25, 0)]
    [InlineData(25, 25, 1)]
    [InlineData(26, 25, 2)]
    [InlineData(50, 25, 2)]
    [InlineData(51, 25, 3)]
    public void PagedResult_TotalPages_is_ceil_total_over_limit(long total, int limit, int expected)
    {
        Assert.Equal(expected, new PagedResult<string>([], 1, limit, total).TotalPages);
    }

    [Fact]
    public void PagedResult_TotalPages_is_zero_when_limit_non_positive()
    {
        Assert.Equal(0, new PagedResult<string>([], 1, 0, 10).TotalPages);
    }

    [Fact]
    public void PagedQuery_defaults_mirror_an_absent_query_string()
    {
        var q = new TestPagedQuery();

        Assert.Equal(1, q.Page);
        Assert.Equal(25, q.Limit);
        Assert.Empty(q.Filters);
        Assert.Empty(q.Sort);
        Assert.Null(q.Search);
    }

    private sealed record TestPagedQuery : PagedQuery;
}
