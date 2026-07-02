extern alias ApiHost;
using ApiHost::Api;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Producer.Domain;

namespace Hosts.Tests;

/// <summary>
/// The producer registration wire-ticket protector (REQ-3.1/14.4): a ticket roundtrips through Protect/Unprotect
/// carrying the verified identity, a tampered or garbage token fails to unprotect, and a token sealed under a
/// different Data Protection purpose cannot be unprotected here (purpose isolation from the OIDC state protector).
/// The token is stateless (no server row); replay safety is the account's unique (Subject) index at submit time.
/// This is the wire-level guard.
/// </summary>
public sealed class ProducerRegistrationTicketsTests
{
    private static ApiHost::Api.ProducerRegistrationTickets Build(IDataProtectionProvider provider) =>
        new(provider, Options.Create(new ProducerRegistrationOptions { TicketTtlMinutes = 10 }));

    private static readonly ProducerTicketPayload Payload = new(
        "g-sub-1", "p@org.com", "org.com", TicketPurpose.Registration);

    [Fact]
    public void A_ticket_roundtrips_carrying_the_verified_identity()
    {
        var tickets = Build(new EphemeralDataProtectionProvider());

        var token = tickets.Protect(Payload);
        var ok = tickets.TryUnprotect(token, out var decoded);

        Assert.True(ok);
        Assert.Equal("g-sub-1", decoded.Subject);
        Assert.Equal("p@org.com", decoded.Email);
        Assert.Equal("org.com", decoded.HostedDomain);
        Assert.Equal(TicketPurpose.Registration, decoded.Purpose);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-token")]
    public void A_garbage_token_is_rejected(string token)
    {
        var tickets = Build(new EphemeralDataProtectionProvider());
        Assert.False(tickets.TryUnprotect(token, out _));
    }

    [Fact]
    public void A_tampered_token_is_rejected()
    {
        var tickets = Build(new EphemeralDataProtectionProvider());
        var token = tickets.Protect(Payload);

        // Flip the last character — the authenticated payload no longer verifies.
        var tampered = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');

        Assert.False(tickets.TryUnprotect(tampered, out _));
    }

    [Fact]
    public void A_token_sealed_under_a_different_purpose_cannot_be_unprotected_here()
    {
        // Same key ring, different purpose (as the OIDC state protector would be) -> not interchangeable (REQ-14.4).
        var provider = new EphemeralDataProtectionProvider();
        var foreign = provider.CreateProtector("Producer.SomethingElse.v1").ToTimeLimitedDataProtector();
        var foreignToken = foreign.Protect("whatever", TimeSpan.FromMinutes(10));

        var tickets = Build(provider);
        Assert.False(tickets.TryUnprotect(foreignToken, out _));
    }
}
