namespace BuildingBlocks.Application;

/// <summary>
/// Escapes SQL Server LIKE metacharacters so a user-supplied filter/search term is matched as literal text,
/// never as a pattern. The four specials <c>\ % _ [</c> are prefixed with the escape char <c>\</c>; callers
/// pass <c>"\\"</c> as the third argument of <c>EF.Functions.Like(col, pattern, "\\")</c>. The paired
/// <c>]</c>/<c>^</c> need no escaping — they are only special INSIDE a <c>[...]</c> class, and once <c>[</c>
/// is escaped that class can never open. One shared definition so every SFS endpoint escapes identically,
/// closing the unescaped-LIKE hole the TS source left on the filter side. (REQ-5.4, REQ-6.4)
/// </summary>
public static class SfsLike
{
    public static string Escape(string value) => value
        .Replace("\\", "\\\\")   // backslash FIRST, else the escapes below get double-escaped
        .Replace("%", "\\%")
        .Replace("_", "\\_")
        .Replace("[", "\\[");
}
