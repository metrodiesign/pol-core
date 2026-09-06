using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Tests;

/// <summary>
/// merchant-psp-settings task 2 — the connection's credential environment and zero-method shape
/// (REQ-2.2, REQ-2.6, REQ-3.3/3.4, REQ-4.8, design "Entity changes").
/// </summary>
public sealed class ConnectionEnvironmentTests
{
    private static readonly Guid MerchantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime At = new(2026, 9, 6, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Zero_method_connection_is_enabled_unknown_health_and_never_eligible()
    {
        var connection = Connection.Create(MerchantId, Code.Omise, "", "psp/omise", At, environment: PspEnvironment.Live);

        Assert.True(connection.IsEnabled);                                  // REQ-3.3
        Assert.Equal(PspConnectionHealth.Unknown, connection.Health);       // REQ-3.4
        Assert.Equal(string.Empty, connection.EnabledMethods);
        Assert.Equal(PspEnvironment.Live, connection.ActiveSecretEnvironment);
        Assert.False(connection.Supports(PaymentMethods.Card));
        Assert.Throws<InvalidOperationException>(() => connection.EnsureEligible(PaymentMethods.Card));

        connection.Update("", null, true);                                  // update may keep it empty
        Assert.Equal(string.Empty, connection.EnabledMethods);
    }

    [Fact]
    public void Create_defaults_to_sandbox_when_no_environment_is_given()
    {
        var connection = Connection.Create(MerchantId, Code.TwoCTwoP, PaymentMethods.Card, "psp/2c2p", At);

        Assert.Equal(PspEnvironment.Sandbox, connection.ActiveSecretEnvironment);
        Assert.Null(connection.PendingSecretEnvironment);
    }

    [Fact]
    public void Activating_a_candidate_adopts_its_environment_and_resets_active_health()
    {
        var connection = Connection.Create(MerchantId, Code.TwoCTwoP, PaymentMethods.Card, "psp/2c2p", At);
        connection.SetInitialSecretVersion(Guid.NewGuid(), PspEnvironment.Sandbox);
        connection.RecordTest(true, "authenticated", At);
        var candidate = Guid.NewGuid();
        connection.StageSecretVersion(candidate, Guid.NewGuid(), PspEnvironment.Live);
        connection.RecordPendingSecretTest(false, At.AddMinutes(1));

        Assert.Equal(PspEnvironment.Live, connection.PendingSecretEnvironment);
        Assert.Equal("probe_failed", connection.PendingSecretTestResult);
        Assert.Equal(PspConnectionHealth.Healthy, connection.Health);      // candidate test never touches active health

        Assert.Equal(candidate, connection.ActivatePendingSecretVersion());

        Assert.Equal(PspEnvironment.Live, connection.ActiveSecretEnvironment);
        Assert.Equal(PspConnectionHealth.Unknown, connection.Health);      // active health reset on activation
        Assert.Null(connection.LastTestResult);
        Assert.Null(connection.PendingSecretEnvironment);
        Assert.Null(connection.PendingSecretTestResult);
        Assert.Null(connection.PendingSecretTestedAt);
    }

    [Fact]
    public void Rejecting_a_candidate_clears_every_pending_field_and_keeps_active_environment()
    {
        var connection = Connection.Create(MerchantId, Code.Omise, PaymentMethods.Card, "psp/omise", At);
        connection.SetInitialSecretVersion(Guid.NewGuid(), PspEnvironment.Sandbox);
        connection.StageSecretVersion(Guid.NewGuid(), Guid.NewGuid(), PspEnvironment.Live);
        connection.RecordPendingSecretTest(true, At);

        connection.RejectPendingSecretVersion();

        Assert.Equal(PspEnvironment.Sandbox, connection.ActiveSecretEnvironment);
        Assert.Null(connection.PendingSecretVersionId);
        Assert.Null(connection.PendingSecretEnvironment);
        Assert.Null(connection.PendingSecretTestResult);
    }

    [Fact]
    public void Webhook_acknowledgement_stores_only_a_hash_of_the_callback_url()
    {
        var connection = Connection.Create(MerchantId, Code.Omise, "", "psp/omise", At);
        var actor = Guid.NewGuid();

        connection.AcknowledgeWebhookRegistration("https://api.example.com/api/v1/webhooks/abc", actor, At);

        Assert.Equal(64, connection.WebhookRegistrationHash!.Length);
        Assert.DoesNotContain("example.com", connection.WebhookRegistrationHash);
        Assert.Equal(actor, connection.WebhookRegisteredBy);
        Assert.Equal(At, connection.WebhookRegisteredAt);
    }

    [Theory]
    [InlineData("sandbox", PspEnvironment.Sandbox)]
    [InlineData(" LIVE ", PspEnvironment.Live)]
    public void Environment_wire_codes_round_trip(string code, PspEnvironment expected)
    {
        Assert.Equal(expected, PspEnvironments.FromCode(code));
        Assert.Equal(code.Trim().ToLowerInvariant(), expected.ToCode());
    }

    [Theory]
    [InlineData("production")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_environment_code_is_a_400_class_argument_error(string? code)
    {
        // REQ-2.6: ArgumentException maps to 400 in ProblemDetailsExceptionHandler; nothing is persisted.
        Assert.Throws<ArgumentException>(() => PspEnvironments.FromCode(code));
    }

    [Theory]
    [InlineData("skey_test_abc", PspEnvironment.Sandbox, true)]
    [InlineData("skey_test_abc", PspEnvironment.Live, false)]
    [InlineData("skey_live_abc", PspEnvironment.Live, true)]
    [InlineData("skey_live_abc", PspEnvironment.Sandbox, false)]
    public void Omise_key_prefix_must_match_environment(string key, PspEnvironment environment, bool matches) =>
        Assert.Equal(matches, OmiseSecretKeys.MatchesEnvironment(key, environment)); // REQ-4.8
}
