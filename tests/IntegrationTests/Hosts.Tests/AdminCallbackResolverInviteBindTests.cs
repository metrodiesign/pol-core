extern alias ApiHost;
using Admins.Application.Users;
using Mediator;
using SharedKernel;

namespace Hosts.Tests;

// Admin callback keeps Microsoft tenant/object identity on a dedicated typed seam; generic providers never enter it.
public sealed class AdminCallbackResolverInviteBindTests
{
    [Fact]
    public async Task Google_identity_never_reaches_invite_bind_or_bootstrap_paths()
    {
        var mediator = new RecordingMediator();
        var resolver = new ApiHost::Api.Admins.CallbackResolver(mediator);

        var result = await resolver.ResolveAtCallbackAsync(
            new ProviderIdentity("google", "google-sub-1"), null, "corr-1", default);

        Assert.Equal(ResolveOutcome.NotFound, result.Outcome);
        Assert.DoesNotContain(mediator.Sent, m => m is BindInvitedCommand);
        Assert.DoesNotContain(mediator.Sent, m => m is SelfProvisionSuperCommand);
    }

    [Fact]
    public async Task Microsoft_identity_is_rejected_by_the_generic_resolver_seam()
    {
        var mediator = new RecordingMediator();
        var resolver = new ApiHost::Api.Admins.CallbackResolver(mediator);

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAtCallbackAsync(
            new ProviderIdentity("MICROSOFT", "mutable-value"), null, "corr-1", default));

        Assert.Empty(mediator.Sent);
    }

    [Fact]
    public async Task Microsoft_identity_dispatches_typed_tenant_object_command_directly()
    {
        var mediator = new RecordingMediator();
        var resolver = new ApiHost::Api.Admins.CallbackResolver(mediator);
        var tenantId = Guid.NewGuid();
        var objectId = Guid.NewGuid();

        await resolver.ResolveMicrosoftAtCallbackAsync(
            tenantId, objectId, "employee@viriyah.co.th", "ZTEST1", "corr-1", default);

        var command = Assert.Single(mediator.Sent.OfType<ResolveMicrosoftAdminCommand>());
        Assert.Equal(tenantId, command.TenantId);
        Assert.Equal(objectId, command.ObjectId);
        Assert.Equal("employee@viriyah.co.th", command.Email);
        Assert.Equal("ZTEST1", command.EmployeeId);
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
