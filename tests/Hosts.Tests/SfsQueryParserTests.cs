extern alias ApiHost;

using BuildingBlocks.Application;
using BuildingBlocks.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using SearchOption = BuildingBlocks.Application.SearchOption;   // disambiguate from System.IO.SearchOption

namespace Hosts.Tests;

/// <summary>
/// Unit contract of the Hosts-layer <c>SfsQueryParser</c>: <c>page</c>/<c>limit</c> are clamped (never a 400),
/// a huge <c>page</c> can never overflow the SQL OFFSET, absent parameters fall back to defaults, over-cap and
/// malformed inputs raise <see cref="ArgumentException"/>, and that exception is mapped to HTTP 400 by the
/// shared <c>ProblemDetailsExceptionHandler</c> — never a 409/500. (REQ-1.1, REQ-1.2, REQ-1.5, REQ-1.6,
/// REQ-2.1, REQ-2.2, REQ-2.3, REQ-2.6, REQ-6.6, REQ-8.1)
/// </summary>
public sealed class SfsQueryParserTests
{
    private static (int Page, int Limit, IReadOnlyList<FilterOption> Filters,
                    IReadOnlyList<SortOption> Sort, SearchOption? Search) Parse(params (string Key, string Value)[] kv)
    {
        var store = new Dictionary<string, StringValues>();
        foreach (var (k, v) in kv) store[k] = v;
        return ApiHost::Api.SfsQueryParser.Parse(new QueryCollection(store));
    }

    private static (int Page, int Limit, IReadOnlyList<FilterOption> Filters,
                    IReadOnlyList<SortOption> Sort, SearchOption? Search) ParseWithMax(
        int maxLimit,
        params (string Key, string Value)[] kv)
    {
        var store = new Dictionary<string, StringValues>();
        foreach (var (key, value) in kv) store[key] = value;
        return ApiHost::Api.SfsQueryParser.Parse(new QueryCollection(store), maxLimit);
    }

    [Fact]
    public void Absent_parameters_fall_back_to_defaults()
    {
        var r = Parse();

        Assert.Equal(1, r.Page);
        Assert.Equal(25, r.Limit);
        Assert.Empty(r.Filters);
        Assert.Empty(r.Sort);
        Assert.Null(r.Search);
    }

    [Theory]
    [InlineData("1000", 25)]    // above ceiling -> 25 (§2 @PageSize, REQ-4.1)
    [InlineData("100", 25)]
    [InlineData("50", 25)]
    [InlineData("25", 25)]
    [InlineData("1", 1)]
    [InlineData("0", 1)]        // below floor -> 1
    [InlineData("-5", 1)]
    [InlineData("abc", 25)]     // unparseable -> default
    public void Limit_is_clamped_into_1_to_25(string limit, int expected)
    {
        Assert.Equal(expected, Parse(("limit", limit)).Limit);
    }

    [Theory]
    [InlineData("100", 100)]
    [InlineData("101", 100)]
    [InlineData("0", 1)]
    public void Endpoint_can_raise_limit_cap_to_100(string limit, int expected)
    {
        Assert.Equal(expected, ParseWithMax(100, ("limit", limit)).Limit);
    }

    [Theory]
    [InlineData("2", 2)]
    [InlineData("1", 1)]
    [InlineData("0", 1)]        // non-positive -> 1 (no negative OFFSET)
    [InlineData("-9", 1)]
    [InlineData("xyz", 1)]      // unparseable -> default
    public void Page_is_clamped_to_at_least_1(string page, int expected)
    {
        Assert.Equal(expected, Parse(("page", page)).Page);
    }

    [Fact]
    public void A_huge_page_cannot_overflow_the_offset()
    {
        var r = Parse(("page", "2000000000"), ("limit", "25"));

        long offset = (long)(r.Page - 1) * r.Limit;
        Assert.InRange(offset, 0, int.MaxValue);            // non-negative AND within int (REQ-2.6)
    }

    [Fact]
    public void Valid_filters_sort_search_are_parsed()
    {
        var r = Parse(
            ("filters", """[{"field":"code","operator":"in","values":["super_admin","support"]}]"""),
            ("sort", """[{"field":"name","order":"DESC"}]"""),
            ("search", """{"query":"admin","fields":["name","description"]}"""));

        var filter = Assert.Single(r.Filters);
        Assert.Equal("code", filter.Field);
        Assert.Equal(FilterOperator.In, filter.Operator);
        Assert.Equal(2, filter.Values!.Length);

        var sort = Assert.Single(r.Sort);
        Assert.Equal("name", sort.Field);
        Assert.Equal(SortDirection.Desc, sort.Order);

        Assert.Equal("admin", r.Search!.Query);
    }

    [Fact]
    public void Too_many_filters_is_rejected()
    {
        var filters = "[" + string.Join(",", Enumerable.Repeat("""{"field":"f","operator":"eq"}""", 51)) + "]";

        Assert.Throws<ArgumentException>(() => Parse(("filters", filters)));
    }

    [Fact]
    public void Too_many_values_in_one_filter_is_rejected()
    {
        var values = "[" + string.Join(",", Enumerable.Range(0, 201)) + "]";
        var filters = $$"""[{"field":"f","operator":"in","values":{{values}}}]""";

        Assert.Throws<ArgumentException>(() => Parse(("filters", filters)));
    }

    [Fact]
    public void Too_many_sort_keys_is_rejected()
    {
        var sort = "[" + string.Join(",", Enumerable.Repeat("""{"field":"f","order":"ASC"}""", 11)) + "]";

        Assert.Throws<ArgumentException>(() => Parse(("sort", sort)));
    }

    [Theory]
    [InlineData("filters", "notjson")]
    [InlineData("sort", "{oops")]
    [InlineData("search", "[[")]
    public void Malformed_json_throws_ArgumentException(string key, string value)
    {
        Assert.Throws<ArgumentException>(() => Parse((key, value)));
    }

    [Fact]
    public async Task Malformed_json_maps_to_400_not_409_or_500()
    {
        var ex = Assert.Throws<ArgumentException>(() => Parse(("filters", "notjson")));

        // ArgumentException, not InvalidOperationException (409) or BadHttpRequestException/IOException (500).
        Assert.IsNotType<InvalidOperationException>(ex);
        Assert.IsNotType<BadHttpRequestException>(ex);
        Assert.Equal(StatusCodes.Status400BadRequest, await MapStatus(ex));
    }

    [Fact]
    public async Task A_numeric_operator_token_maps_to_400()
    {
        // Integer enum values are rejected (allowIntegerValues:false), so a numeric operator is malformed -> 400,
        // never silently accepted as the ordinal (REQ-1.3).
        var ex = Assert.Throws<ArgumentException>(() =>
            Parse(("filters", """[{"field":"status","operator":0,"value":"active"}]""")));

        Assert.Equal(StatusCodes.Status400BadRequest, await MapStatus(ex));
    }

    private static async Task<int> MapStatus(Exception exception)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        await using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Headers.Accept = "application/json";
        context.Response.Body = new MemoryStream();

        var handler = new ProblemDetailsExceptionHandler(
            provider.GetRequiredService<IProblemDetailsService>(),
            NullLogger<ProblemDetailsExceptionHandler>.Instance);

        await handler.TryHandleAsync(context, exception, CancellationToken.None);
        return context.Response.StatusCode;
    }
}
