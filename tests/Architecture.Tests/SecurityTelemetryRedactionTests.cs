using System.Text.RegularExpressions;

namespace Architecture.Tests;

/// <summary>
/// Static scan gate for rls-to-query-filter REQ-13.2/REQ-13.4: <c>DenialEvent.Reason</c> must always be a
/// short, fixed, developer-authored string literal (see <c>ISecurityTelemetry.cs</c>'s doc comment) — never
/// an interpolated string (which could carry a value into the sink) and never an exception's <c>.Message</c>
/// (which can carry SQL text, connection details, or other operator/attacker-controlled content). Every
/// <c>Emit(...)</c> call site in <c>src/</c> — both direct <c>ISecurityTelemetry.Emit(new DenialEvent(...))</c>
/// calls and the per-file private <c>Emit(category, reason)</c> helpers that forward into one — is scanned as
/// one statement (from the <c>Emit(</c> token to its closing <c>;</c>) and must contain neither pattern.
/// </summary>
public sealed class SecurityTelemetryRedactionTests
{
    // Lazy, length-capped: a real Emit(...) statement in this codebase never exceeds a few hundred
    // characters, so this cannot run away and swallow the rest of the file if some call site has no
    // trailing ';' within reach (e.g. an empty `{ }` body).
    private static readonly Regex EmitStatement = new(@"\bEmit\([\s\S]{0,600}?;", RegexOptions.Compiled);

    [Fact]
    public void No_Emit_call_site_in_src_interpolates_a_string_or_forwards_an_exception_message()
    {
        var repoRoot = FindRepoRoot();
        var srcRoot = Path.Combine(repoRoot, "src");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;

            var text = File.ReadAllText(file);
            var relative = Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');

            foreach (Match m in EmitStatement.Matches(text))
            {
                if (IsTainted(m.Value))
                    offenders.Add(relative);
            }
        }

        Assert.True(offenders.Count == 0,
            "An Emit(...) call site passes an interpolated string ($\"...\") or an exception .Message as the "
            + "DenialEvent reason — REQ-13.2 requires Reason to be a short, fixed, developer-authored literal "
            + "(no PII/secret can leak through). Offenders: " + string.Join(", ", offenders.Distinct()));
    }

    // Proves the detector itself actually catches both banned patterns, not just that no offenders exist today.
    [Theory]
    [InlineData("Emit(DenialCategory.GuardDenial, $\"denied for {subject}\");", true)]
    [InlineData("Emit(DenialCategory.GuardDenial, ex.Message);", true)]
    [InlineData("Emit(DenialCategory.GuardDenial, \"A stale/forged concurrency token was rejected at commit.\");", false)]
    public void Detector_flags_interpolation_and_exception_message_but_not_a_plain_literal(string statement, bool expectTainted) =>
        Assert.Equal(expectTainted, IsTainted(statement));

    private static bool IsTainted(string statement) => statement.Contains("$\"") || statement.Contains(".Message");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pol-core.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "Could not locate repo root (pol-core.slnx) from " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
