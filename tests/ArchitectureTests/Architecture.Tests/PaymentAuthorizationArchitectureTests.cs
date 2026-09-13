using System.Text.RegularExpressions;
using Payments.Domain.Capabilities;

namespace Architecture.Tests;

public sealed class PaymentAuthorizationArchitectureTests
{
    [Fact]
    public void OrderPaymentAuthorization_keeps_the_legacy_cart_writer_and_canonical_command_producer()
    {
        var root = FindRepoRoot();
        var calls = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => Regex.IsMatch(File.ReadAllText(path), @"\bOrder\.Create\s*\("))
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .ToArray();

        Assert.Equal(["src/Api/Api/Orders/OrderCreationCoordinator.cs"], calls);
        var legacyCallers = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("new CreateOrderCommand(", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .ToArray();
        Assert.Contains("src/Api/Api/Program.cs", legacyCallers);
        Assert.DoesNotContain("src/Api/Api/Orders/OrderCreationCoordinator.cs", legacyCallers);
    }

    [Fact]
    public void PaymentAuthorization_creation_paths_lock_then_resolve_then_write()
    {
        var root = FindRepoRoot();
        AssertOrdered(Read(root, "src/Api/Api/Orders/OrderCreationCoordinator.cs"),
            "AcquireMerchantSharedAsync", "ResolveMethodAsync", "Order.Create(");

        var createSession = Slice(Read(root,
                "src/Application/Modules/Payments.Application/CreateSession/CreateSessionHandler.cs"),
            "private async Task<MintResult> MintUnderOrderLockAsync",
            "private async Task<MintResult> ResumeCreatedUnderOrderLockAsync");
        AssertOrdered(createSession, "AcquireMerchantSharedAsync", "GetForMintAsync",
            "EnsureAuthorizedAsync", "Session.Create(", "_sessions.Add(");

        var redirect = Slice(Read(root,
                "src/Application/Modules/Payments.Application/StartRedirect/StartRedirectHandler.cs"),
            "private async Task<Connection> ClaimFirstRedirectAsync",
            "private async Task FailSessionAsync");
        AssertOrdered(redirect, "AcquireMerchantSharedAsync", "GetForMintAsync",
            "ResolveMethodAsync", "session.BeginRedirect", "SaveChangesAsync");
    }

    [Fact]
    public void PaymentAuthorization_lock_order_is_global_before_Merchant_and_provisioning_participates()
    {
        var root = FindRepoRoot();
        var locks = Read(root,
            "src/Infrastructure/Persistence/Persistence.MerchantRuntime/Payments/PaymentAuthorizationSqlLockManager.cs");
        AssertOrdered(Slice(locks, "AcquireMerchantSharedAsync", "AcquireMerchantExclusiveAsync"),
            "payment-authz:global", "payment-authz:merchant:");
        AssertOrdered(Slice(locks, "AcquireMerchantExclusiveAsync", "private async Task AcquireAsync"),
            "payment-authz:global", "payment-authz:merchant:");

        Assert.Contains("AcquireMerchantExclusiveAsync", Read(root,
            "src/Infrastructure/Persistence/Persistence.Provisioning/ProvisioningCoordinator.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void PaymentAuthorization_context_ownership_remains_disjoint()
    {
        using var control = PaymentCapabilityModel.ControlPlane();
        using var runtime = PaymentCapabilityModel.MerchantRuntime();

        Assert.NotNull(control.Model.FindEntityType(typeof(PaymentAuthorizationState)));
        Assert.Null(runtime.Model.FindEntityType(typeof(PaymentAuthorizationState)));
        Assert.NotNull(control.Model.FindEntityType(typeof(MerchantPaymentMethod)));
        Assert.Null(runtime.Model.FindEntityType(typeof(MerchantPaymentMethod)));
    }

    private static void AssertOrdered(string source, params string[] tokens)
    {
        var offset = 0;
        foreach (var token in tokens)
        {
            var index = source.IndexOf(token, offset, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Expected '{token}' after offset {offset}.");
            offset = index + token.Length;
        }
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        var endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(startIndex >= 0 && endIndex > startIndex, $"Could not slice '{start}' to '{end}'.");
        return source[startIndex..endIndex];
    }

    private static string Read(string root, string relative) =>
        File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pol-core.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
