using System.Reflection;
using Persistence.ControlPlane;
using Persistence.MerchantRuntime;
using Persistence.MerchantUsers;

namespace Architecture.Tests;

/// <summary>
/// Reflection guardrail for rls-to-query-filter REQ-2.10: every runtime context must seal all 4
/// <c>SaveChanges</c> overloads so a write can never reach the database except through
/// <c>GuardedRuntimeDbContext</c>'s save-core. <c>MethodInfo.IsFinal</c> is true only when the JIT-visible
/// vtable slot cannot be overridden further — exactly what "sealed override" produces, even though the
/// override lives on the shared base class rather than being re-declared per context.
/// </summary>
public sealed class WriteGuardSealTests
{
    private static readonly Type[] RuntimeContexts =
    [
        typeof(ControlPlaneDbContext),
        typeof(MerchantUserDbContext),
        typeof(MerchantRuntimeDbContext),
    ];

    private static readonly (string Name, Type[] ParameterTypes)[] SaveChangesOverloads =
    [
        ("SaveChanges", []),
        ("SaveChanges", [typeof(bool)]),
        ("SaveChangesAsync", [typeof(CancellationToken)]),
        ("SaveChangesAsync", [typeof(bool), typeof(CancellationToken)]),
    ];

    [Fact]
    public void Every_runtime_context_seals_all_four_SaveChanges_overloads()
    {
        var unsealed_ = new List<string>();

        foreach (var context in RuntimeContexts)
        foreach (var (name, parameterTypes) in SaveChangesOverloads)
        {
            var method = context.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, parameterTypes)
                ?? throw new InvalidOperationException($"{context.Name} has no {name}({string.Join(", ", parameterTypes.Select(t => t.Name))}) overload.");

            if (!method.IsFinal)
                unsealed_.Add($"{context.Name}.{name}({string.Join(", ", parameterTypes.Select(t => t.Name))})");
        }

        Assert.True(unsealed_.Count == 0,
            "SaveChanges overload(s) not sealed — a derived context could weaken the write guard: " + string.Join(", ", unsealed_));
    }
}
