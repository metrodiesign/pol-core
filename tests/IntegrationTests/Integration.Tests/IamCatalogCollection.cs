namespace Integration.Tests;

/// <summary>
/// Serialises the two rf2 IAM integration classes relative to each other: both mutate the single shared
/// <c>iam.*</c> catalog on the live DB, and <see cref="IamCatalogGrantsTests"/>'s absolute seed-count /
/// SetEquals drift assertions would otherwise race <see cref="IamRoleResolutionTests"/>'s transient probe rows
/// under xUnit's default per-class parallelism. Other integration classes never touch <c>iam.*</c>, so they
/// stay parallel.
/// </summary>
[CollectionDefinition(Name)]
public sealed class IamCatalogCollection
{
    public const string Name = "iam-catalog";
}
