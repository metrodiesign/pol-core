using Payments.Domain;
using Payments.Domain.Psp;

namespace Payments.Tests;

/// <summary>
/// Pure domain tests for <see cref="Connection.EnsureEligible"/> — the single gate both the create-session
/// and the start-redirect paths ask "may this connection charge this method right now". Server state (a
/// disabled connection, a method the company never enabled) must be InvalidOperationException (409), while
/// malformed input stays ArgumentException (400), and no message may carry the secret ref.
/// </summary>
public sealed class ConnectionEligibilityTests
{
    private const string SecretRefName = "psp/2c2p/merchant-1";

    private static readonly Guid MerchantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime At = new(2026, 7, 26, 9, 0, 0, DateTimeKind.Utc);

    private static Connection NewConnection(string enabledMethods = "card,promptpay") =>
        Connection.Create(MerchantId, Code.TwoCTwoP, enabledMethods, SecretRefName, At);

    /// <summary>A connection an admin has turned off. <see cref="Connection"/> has no Disable() — IsEnabled
    /// only ever goes false by EF materialising such a row — so the test reproduces that state the way EF
    /// does (through the private setter) instead of adding a production method no caller wants.</summary>
    private static Connection DisabledConnection(string enabledMethods = "card")
    {
        var connection = NewConnection(enabledMethods);
        typeof(Connection).GetProperty(nameof(Connection.IsEnabled))!.SetValue(connection, false);
        return connection;
    }

    [Theory]
    [InlineData("card")]
    [InlineData("promptpay")]
    public void EnsureEligible_passes_for_an_enabled_method_on_an_enabled_connection(string method)
    {
        var connection = NewConnection();

        connection.EnsureEligible(method);
    }

    [Fact]
    public void EnsureEligible_rejects_a_disabled_connection_without_naming_the_secret_ref()
    {
        var connection = DisabledConnection();

        // Even a method the connection DOES enable must be refused while the connection is off.
        var ex = Assert.Throws<InvalidOperationException>(() => connection.EnsureEligible("card"));

        Assert.Contains(connection.Id.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretRefName, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("installment")]  // canonical, but not in this connection's list
    [InlineData("Card")]         // right method, wrong case: EnabledMethods compares ordinally
    public void EnsureEligible_rejects_a_method_the_connection_does_not_enable(string method)
    {
        var connection = NewConnection();

        var ex = Assert.Throws<InvalidOperationException>(() => connection.EnsureEligible(method));

        Assert.DoesNotContain(SecretRefName, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EnsureEligible_rejects_a_blank_method_as_bad_input(string method)
    {
        var connection = NewConnection();

        Assert.Throws<ArgumentException>(() => connection.EnsureEligible(method));
    }

    [Fact]
    public void EnsureEligible_rejects_null_as_bad_input()
    {
        var connection = NewConnection();

        Assert.Throws<ArgumentNullException>(() => connection.EnsureEligible(null!));
    }

    [Fact]
    public void EnsureEligible_checks_the_disabled_state_before_the_method_list()
    {
        // A disabled connection whose list also lacks the method: the reported reason must be the
        // connection being off, so ops are not sent to fix EnabledMethods on a connection nobody enabled.
        var connection = DisabledConnection(PaymentMethods.Card);

        var ex = Assert.Throws<InvalidOperationException>(() => connection.EnsureEligible(PaymentMethods.PromptPay));

        Assert.Contains("disabled", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Secret_rotation_keeps_one_active_and_one_pending_version()
    {
        var connection = NewConnection();
        var active = Guid.NewGuid();
        var candidate = Guid.NewGuid();
        var approval = Guid.NewGuid();

        connection.SetInitialSecretVersion(active);
        connection.StageSecretVersion(candidate, approval);

        Assert.Equal(active, connection.ActiveSecretVersionId);
        Assert.Equal(candidate, connection.PendingSecretVersionId);
        Assert.Equal(approval, connection.PendingApprovalId);

        Assert.Equal(candidate, connection.ActivatePendingSecretVersion());
        Assert.Equal(candidate, connection.ActiveSecretVersionId);
        Assert.Null(connection.PendingSecretVersionId);
        Assert.Null(connection.PendingApprovalId);
        Assert.Equal(4, connection.Version);
    }

    [Fact]
    public void A_second_pending_secret_is_rejected()
    {
        var connection = NewConnection();
        connection.StageSecretVersion(Guid.NewGuid(), Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() =>
            connection.StageSecretVersion(Guid.NewGuid(), Guid.NewGuid()));
    }
}
