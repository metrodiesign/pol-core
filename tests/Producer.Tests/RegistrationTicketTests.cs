using Producer.Domain;

namespace Producer.Tests;

/// <summary>The <see cref="RegistrationTicket"/> single-use guard (REQ-3): a fresh, unexpired ticket consumes
/// exactly once; a second consume or an expired ticket is rejected and stamps nothing.</summary>
public sealed class RegistrationTicketTests
{
    private static readonly DateTime Now = new(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private static RegistrationTicket NewTicket(TicketPurpose purpose = TicketPurpose.Registration) =>
        RegistrationTicket.Issue("g-sub-1", "p@org.com", "org.com", purpose, Now, Ttl);

    [Fact]
    public void Issue_sets_identity_purpose_and_expiry()
    {
        var ticket = NewTicket(TicketPurpose.Correction);

        Assert.Equal("g-sub-1", ticket.Subject);
        Assert.Equal("p@org.com", ticket.Email);
        Assert.Equal("org.com", ticket.HostedDomain);
        Assert.Equal(TicketPurpose.Correction, ticket.Purpose);
        Assert.Equal(Now, ticket.CreatedAt);
        Assert.Equal(Now + Ttl, ticket.ExpiresAt);
        Assert.Null(ticket.UsedAt);
    }

    [Fact]
    public void Consume_succeeds_once_on_a_fresh_ticket_and_stamps_used_at()
    {
        var ticket = NewTicket();

        var consumed = ticket.Consume(Now);

        Assert.True(consumed);
        Assert.Equal(Now, ticket.UsedAt);
    }

    [Fact]
    public void Consume_a_second_time_is_rejected()
    {
        var ticket = NewTicket();
        Assert.True(ticket.Consume(Now));
        var firstUsedAt = ticket.UsedAt;

        var second = ticket.Consume(Now + TimeSpan.FromSeconds(1));

        Assert.False(second); // no double-use (REQ-3.3)
        Assert.Equal(firstUsedAt, ticket.UsedAt); // unchanged
    }

    [Fact]
    public void Consume_at_or_after_expiry_is_rejected()
    {
        var ticket = NewTicket();

        var atExpiry = ticket.Consume(ticket.ExpiresAt); // now >= ExpiresAt (REQ-3.6)

        Assert.False(atExpiry);
        Assert.Null(ticket.UsedAt);
    }

    [Fact]
    public void Consume_after_expiry_is_rejected()
    {
        var ticket = NewTicket();

        var afterExpiry = ticket.Consume(ticket.ExpiresAt + TimeSpan.FromMinutes(1));

        Assert.False(afterExpiry);
        Assert.Null(ticket.UsedAt);
    }

    [Fact]
    public void Consume_just_before_expiry_succeeds()
    {
        var ticket = NewTicket();

        var consumed = ticket.Consume(ticket.ExpiresAt - TimeSpan.FromSeconds(1));

        Assert.True(consumed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Issue_rejects_a_blank_subject(string? subject) =>
        Assert.ThrowsAny<ArgumentException>(() =>
            RegistrationTicket.Issue(subject!, "p@org.com", null, TicketPurpose.Registration, Now, Ttl));

    [Fact]
    public void Issue_rejects_a_non_positive_ttl() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RegistrationTicket.Issue("g-sub-1", "p@org.com", null, TicketPurpose.Registration, Now, TimeSpan.Zero));
}
