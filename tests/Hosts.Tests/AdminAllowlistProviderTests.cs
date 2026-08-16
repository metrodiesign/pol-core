extern alias ApiHost;
using Admins.Application.Users;
using Mediator;
using Microsoft.Extensions.Configuration;

namespace Hosts.Tests;

// AdminAllowlist:Subjects entries are "provider:subject"; a bare entry means "google" (backward compat with
// values already deployed — REQ-4.3), and the CURRENT login's provider must match the entry's prefix before
// self-provision (REQ-4.4). A subject from one provider must never satisfy another provider's entry.
public sealed class AdminAllowlistProviderTests
{
    [Theory]
    [InlineData("google-sub-1", "google", "google-sub-1", true)]              // bare entry -> google
    [InlineData("google-sub-1", "microsoft", "google-sub-1", false)]           // bare entry never matches microsoft
    [InlineData("microsoft:entra-oid-1", "microsoft", "entra-oid-1", true)]
    [InlineData("microsoft:entra-oid-1", "google", "entra-oid-1", false)]      // provider mismatch fails closed
    [InlineData("Microsoft:entra-oid-1", "microsoft", "entra-oid-1", true)]    // entry prefix is case-normalized
    [InlineData("google:google-sub-1", "google", "google-sub-1", true)]
    [InlineData("microsoft:entra-oid-1", "microsoft", "entra-oid-2", false)]
    public void An_allowlist_entry_matches_only_its_own_provider_and_subject(
        string entry, string provider, string subject, bool expected) =>
        Assert.Equal(expected, ApiHost::Api.Admins.CallbackResolver.AllowlistEntryMatches(entry, provider, subject));

    [Fact]
    public async Task A_matching_subject_under_the_wrong_provider_is_not_provisioned()
    {
        var mediator = new RecordingMediator();
        var resolver = new ApiHost::Api.Admins.CallbackResolver(mediator, Config("google-sub-1"));

        // Same subject string arriving via microsoft must NOT satisfy the bare (=google) entry (REQ-4.4).
        var result = await resolver.ResolveAtCallbackAsync(
            "microsoft", "google-sub-1", "ops@org.com", emailVerified: false, "corr-1", default);

        Assert.Equal(ResolveOutcome.NotFound, result.Outcome);
        Assert.DoesNotContain(mediator.Sent, m => m is SelfProvisionSuperCommand);
    }

    [Fact]
    public async Task A_prefixed_entry_provisions_a_matching_microsoft_login_with_its_provider()
    {
        var mediator = new RecordingMediator();
        var resolver = new ApiHost::Api.Admins.CallbackResolver(mediator, Config("microsoft:entra-oid-1"));

        var result = await resolver.ResolveAtCallbackAsync(
            "microsoft", "entra-oid-1", "ops@org.com", emailVerified: false, "corr-1", default);

        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
        var provision = Assert.Single(mediator.Sent.OfType<SelfProvisionSuperCommand>());
        Assert.Equal("microsoft", provision.Provider);
        Assert.Equal("entra-oid-1", provision.Subject);
    }

    private static IConfiguration Config(params string[] entries)
    {
        var pairs = entries.Select((e, i) => new KeyValuePair<string, string?>($"AdminAllowlist:Subjects:{i}", e));
        return new ConfigurationBuilder().AddInMemoryCollection(pairs).Build();
    }

    /// <summary>Answers NotFound for resolve/bind and a Resolution for self-provision, recording every Send.</summary>
    private sealed class RecordingMediator : IMediator
    {
        public List<object> Sent { get; } = [];

        private ValueTask<T> Record<T>(object message)
        {
            Sent.Add(message);
            object answer = message is SelfProvisionSuperCommand
                ? new Resolution(Guid.NewGuid(), "ops@org.com", Admins.Domain.Users.Tier.Super, AccessibleMerchants.All)
                : ResolveResult.NotFound;
            return new ValueTask<T>((T)answer);
        }

        public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default) => Record<TResponse>(request);
        public ValueTask<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken ct = default) => Record<TResponse>(command);
        public ValueTask<TResponse> Send<TResponse>(IQuery<TResponse> query, CancellationToken ct = default) => Record<TResponse>(query);
        public ValueTask<object?> Send(object message, CancellationToken ct = default) => Record<object?>(message);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamCommand<TResponse> command, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamQuery<TResponse> query, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object message, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask Publish<TNotification>(TNotification n, CancellationToken ct = default) where TNotification : INotification => throw new NotSupportedException();
        public ValueTask Publish(object n, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
