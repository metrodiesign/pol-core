extern alias ApiHost;
using Admins.Application.Users;
using Mediator;
using SharedKernel;

namespace Hosts.Tests;

// Admin callback no longer binds invites or bootstraps Google identities. Eligible Microsoft identities enter the
// application JIT command; eligibility itself is enforced before this resolver by MicrosoftWorkforceClaimsValidator.
public sealed class AdminCallbackResolverInviteBindTests
{
    [Fact]
    public async Task Google_identity_never_reaches_invite_bind_or_bootstrap_paths()
    {
        var mediator = new RecordingMediator();
        var resolver = new ApiHost::Api.Admins.CallbackResolver(mediator);

        var result = await resolver.ResolveAtCallbackAsync(
            new ProviderIdentity("google", "google-sub-1"),
            "victim-invite@org.com", emailVerified: true, "corr-1", default);

        Assert.Equal(ResolveOutcome.NotFound, result.Outcome);
        Assert.DoesNotContain(mediator.Sent, m => m is BindInvitedCommand);
        Assert.DoesNotContain(mediator.Sent, m => m is SelfProvisionSuperCommand);
    }

    [Fact]
    public async Task Microsoft_unknown_identity_dispatches_typed_jit_command_without_email_binding()
    {
        var mediator = new RecordingMediator();
        var resolver = new ApiHost::Api.Admins.CallbackResolver(mediator);

        await resolver.ResolveAtCallbackAsync(
            new ProviderIdentity("microsoft", "abcdefab-cdef-4abc-8def-abcdefabcdef"),
            "invited@org.com", emailVerified: false, "corr-1", default);

        var jit = Assert.Single(mediator.Sent.OfType<JitProvisionMicrosoftAdminCommand>());
        Assert.Equal("invited@org.com", jit.Email);
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
