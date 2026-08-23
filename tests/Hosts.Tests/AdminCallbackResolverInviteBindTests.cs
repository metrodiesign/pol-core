extern alias ApiHost;
using Admins.Application.Users;
using Mediator;
using SharedKernel;

namespace Hosts.Tests;

// Admin callback no longer binds invites or bootstraps Google identities. Eligible Microsoft identities enter one
// canonical resolve/bind/JIT command; eligibility is enforced before this resolver.
public sealed class AdminCallbackResolverInviteBindTests
{
    [Fact]
    public async Task Google_identity_never_reaches_invite_bind_or_bootstrap_paths()
    {
        var mediator = new RecordingMediator();
        var resolver = new ApiHost::Api.Admins.CallbackResolver(mediator);

        var result = await resolver.ResolveAtCallbackAsync(
            new ProviderIdentity("google", "google-sub-1"), "corr-1", default);

        Assert.Equal(ResolveOutcome.NotFound, result.Outcome);
        Assert.DoesNotContain(mediator.Sent, m => m is BindInvitedCommand);
        Assert.DoesNotContain(mediator.Sent, m => m is SelfProvisionSuperCommand);
    }

    [Fact]
    public async Task Microsoft_identity_dispatches_canonical_resolve_command_directly()
    {
        var mediator = new RecordingMediator();
        var resolver = new ApiHost::Api.Admins.CallbackResolver(mediator);

        await resolver.ResolveAtCallbackAsync(
            new ProviderIdentity("microsoft", "employee@viriyah.co.th"), "corr-1", default);

        var command = Assert.Single(mediator.Sent.OfType<ResolveMicrosoftAdminCommand>());
        Assert.Equal("employee@viriyah.co.th", command.CanonicalEmail);
        Assert.DoesNotContain(mediator.Sent, message => message is ResolveQuery);
        Assert.DoesNotContain(mediator.Sent, m => m is BindInvitedCommand);
    }

    /// <summary>Records every Send and answers NotFound for both the subject lookup and the bind attempt.</summary>
    private sealed class RecordingMediator : AnsweringMediator
    {
        public List<object> Sent { get; } = [];

        protected override object? Answer(object message)
        {
            Sent.Add(message);
            return ResolveResult.NotFound;
        }
    }
}
