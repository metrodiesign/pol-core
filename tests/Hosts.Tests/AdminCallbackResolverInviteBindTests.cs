extern alias ApiHost;
using Admins.Application.Users;
using Mediator;
using Microsoft.Extensions.Configuration;

namespace Hosts.Tests;

// Codex review (PR #123) High #2: invite binding keys on the id_token email, and Entra's email/preferred_username
// are MUTABLE, UNVERIFIED claims — an org user who sets their mail/UPN to an unbound invite's address must NOT be
// able to bind that admin account to their own subject. The resolver may attempt BindInvited ONLY when the
// provider attested the email (Google's email_verified gate); an unverified email is display-only.
public sealed class AdminCallbackResolverInviteBindTests
{
    [Fact]
    public async Task An_unverified_email_never_reaches_the_invite_bind_path()
    {
        var mediator = new RecordingMediator();
        var resolver = new ApiHost::Api.Admins.CallbackResolver(mediator, EmptyConfig());

        var result = await resolver.ResolveAtCallbackAsync(
            "microsoft", "entra-oid-1", "victim-invite@org.com", emailVerified: false, "corr-1", default);

        Assert.Equal(ResolveOutcome.NotFound, result.Outcome); // no bind, no self-provision (empty allowlist)
        Assert.DoesNotContain(mediator.Sent, m => m is BindInvitedCommand);
    }

    [Fact]
    public async Task A_provider_verified_email_still_attempts_the_invite_bind()
    {
        var mediator = new RecordingMediator();
        var resolver = new ApiHost::Api.Admins.CallbackResolver(mediator, EmptyConfig());

        await resolver.ResolveAtCallbackAsync(
            "google", "google-sub-1", "invited@org.com", emailVerified: true, "corr-1", default);

        var bind = Assert.Single(mediator.Sent.OfType<BindInvitedCommand>());
        Assert.Equal("invited@org.com", bind.Email);
    }

    private static IConfiguration EmptyConfig() => new ConfigurationBuilder().Build();

    /// <summary>Records every Send and answers NotFound for both the subject lookup and the bind attempt.</summary>
    private sealed class RecordingMediator : IMediator
    {
        public List<object> Sent { get; } = [];

        private ValueTask<T> Record<T>(object message)
        {
            Sent.Add(message);
            return new ValueTask<T>((T)(object)ResolveResult.NotFound);
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
