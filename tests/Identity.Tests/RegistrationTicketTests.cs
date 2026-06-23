using Identity.Domain;

namespace Identity.Tests;

public sealed class RegistrationTicketTests
{
    private static readonly DateTime Now = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    [Fact]
    public void Issue_sets_expiry_and_is_initially_usable()
    {
        var ticket = RegistrationTicket.Issue("sub", "a@example.com", "example.com", Now, Ttl);

        Assert.Equal(Now + Ttl, ticket.ExpiresAtUtc);
        Assert.Null(ticket.UsedAtUtc);
        Assert.True(ticket.IsUsable(Now));
    }

    [Fact]
    public void Consume_marks_used_and_blocks_replay()
    {
        var ticket = RegistrationTicket.Issue("sub", "a@example.com", null, Now, Ttl);

        ticket.Consume(Now.AddMinutes(1));

        Assert.NotNull(ticket.UsedAtUtc);
        Assert.False(ticket.IsUsable(Now.AddMinutes(2)));
        Assert.Throws<InvalidOperationException>(() => ticket.Consume(Now.AddMinutes(2))); // replay
    }

    [Fact]
    public void Consume_rejects_an_expired_ticket()
    {
        var ticket = RegistrationTicket.Issue("sub", "a@example.com", null, Now, Ttl);

        Assert.False(ticket.IsUsable(Now + Ttl));
        Assert.Throws<InvalidOperationException>(() => ticket.Consume(Now + Ttl + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Issue_rejects_non_positive_ttl() =>
        Assert.Throws<ArgumentException>(() => RegistrationTicket.Issue("sub", "a@example.com", null, Now, TimeSpan.Zero));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Issue_rejects_blank_subject(string? subject) =>
        Assert.ThrowsAny<ArgumentException>(() => RegistrationTicket.Issue(subject!, "a@example.com", null, Now, Ttl));
}
