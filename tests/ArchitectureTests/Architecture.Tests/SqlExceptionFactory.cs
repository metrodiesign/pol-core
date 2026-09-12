using System.Reflection;
using Microsoft.Data.SqlClient;

namespace Architecture.Tests;

/// <summary>
/// Fabricates a real <see cref="SqlException"/> carrying a chosen Number/State/Class through SqlClient's
/// internal factory — the type has no public constructor, and only a live SQL Server can raise one
/// naturally. Used by the probe-dependency-failure-mapping tests to prove classification without a server:
/// the unique-violation numbers (2627/2601) that must STAY a 409, and the guard's Number/State/Class
/// message composition (REQ-4.1).
/// </summary>
internal static class SqlExceptionFactory
{
    public static SqlException Create(int number, byte state = 1, byte errorClass = 20)
    {
        var collection = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection)
            .GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(collection, [CreateError(number, state, errorClass)]);

        var create = typeof(SqlException).GetMethod(
            "CreateException", BindingFlags.NonPublic | BindingFlags.Static, binder: null,
            [typeof(SqlErrorCollection), typeof(string)], modifiers: null)!;
        return (SqlException)create.Invoke(null, [collection, "16.0.0"])!;
    }

    private static SqlError CreateError(int number, byte state, byte errorClass)
    {
        var ctor = typeof(SqlError).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var args = ctor.GetParameters()
            .Select(p => p.Name switch
            {
                "infoNumber" => (object?)number,
                "errorState" => state,
                "errorClass" => errorClass,
                _ when p.ParameterType == typeof(string) => "fabricated",
                _ when p.ParameterType.IsValueType => Activator.CreateInstance(p.ParameterType),
                _ => null,
            })
            .ToArray();
        return (SqlError)ctor.Invoke(args);
    }
}
