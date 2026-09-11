using System.Text.Json;

namespace BuildingBlocks.Application;

/// <summary>
/// One parsed filter clause from the SFS <c>filters</c> array: a whitelisted <paramref name="Field"/>, an
/// <paramref name="Operator"/>, and its argument as raw JSON. Scalar operators carry <paramref name="Value"/>;
/// set operators (<c>in</c>/<c>not_in</c>/<c>between</c>) carry <paramref name="Values"/>. The argument is kept
/// as <see cref="JsonElement"/> — not <c>object</c> — because the target CLR type depends on the field and is
/// only known at apply time, after the whitelist check. (REQ-9.1, REQ-9.3)
/// </summary>
public sealed record FilterOption(
    string Field,
    FilterOperator Operator,
    JsonElement? Value = null,
    JsonElement[]? Values = null);
