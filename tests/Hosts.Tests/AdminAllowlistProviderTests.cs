extern alias ApiHost;
using Admins.Application.Users;
using Mediator;
using Microsoft.Extensions.Configuration;
using SharedKernel;

namespace Hosts.Tests;

// AdminAllowlist:Subjects entries are "provider:subject"; a bare entry means "google" (backward compat with
// values already deployed — REQ-4.3), and the CURRENT login's provider must match the entry's prefix before
// self-provision (REQ-4.4). A subject from one provider must never satisfy another provider's entry.
public sealed class AdminAllowlistProviderTests
{
    [Theory]
    [InlineData("google-sub-1", "google", "google-sub-1", true)]              // bare entry -> google
    [InlineData("google-sub-1", "microsoft", "google-sub-1", false)]           // bare entry never matches microsoft
    [InlineData("microsoft:ABCDEFAB-CDEF-4ABC-8DEF-ABCDEFABCDEF", "microsoft", "abcdefab-cdef-4abc-8def-abcdefabcdef", true)]
    [InlineData("microsoft:abcdefab-cdef-4abc-8def-abcdefabcdef", "google", "abcdefab-cdef-4abc-8def-abcdefabcdef", false)]
    [InlineData("Microsoft:abcdefab-cdef-4abc-8def-abcdefabcdef", "microsoft", "ABCDEFAB-CDEF-4ABC-8DEF-ABCDEFABCDEF", true)]
    [InlineData("google:google-sub-1", "google", "google-sub-1", true)]
    [InlineData("microsoft:abcdefab-cdef-4abc-8def-abcdefabcdef", "microsoft", "fedcbafe-dcba-4fed-8cba-fedcbafedcba", false)]
    [InlineData("microsoft:not-a-guid", "microsoft", "abcdefab-cdef-4abc-8def-abcdefabcdef", false)]
    public void An_allowlist_entry_matches_only_its_own_provider_and_subject(
        string entry, string provider, string subject, bool expected) =>
        Assert.Equal(expected, ApiHost::Api.Admins.CallbackResolver.AllowlistEntryMatches(entry, new ProviderIdentity(provider, subject)));

    [Fact]
    public async Task A_matching_subject_under_the_wrong_provider_is_not_provisioned()
    {
        var mediator = new RecordingMediator();
        var resolver = new ApiHost::Api.Admins.CallbackResolver(mediator, Config("google-sub-1"));

        // Same subject string arriving via microsoft must NOT satisfy the bare (=google) entry (REQ-4.4).
        var result = await resolver.ResolveAtCallbackAsync(
            new ProviderIdentity("microsoft", "google-sub-1"), "ops@org.com", emailVerified: false, "corr-1", default);

        Assert.Equal(ResolveOutcome.NotFound, result.Outcome);
        Assert.DoesNotContain(mediator.Sent, m => m is SelfProvisionSuperCommand);
    }

    [Fact]
    public async Task A_prefixed_entry_provisions_a_matching_microsoft_login_with_its_provider()
    {
        var mediator = new RecordingMediator();
        const string subject = "abcdefab-cdef-4abc-8def-abcdefabcdef";
        var resolver = new ApiHost::Api.Admins.CallbackResolver(mediator, Config($"microsoft:{subject}"));

        var result = await resolver.ResolveAtCallbackAsync(
            new ProviderIdentity("microsoft", subject), "ops@org.com", emailVerified: false, "corr-1", default);

        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
        var provision = Assert.Single(mediator.Sent.OfType<SelfProvisionSuperCommand>());
        Assert.Equal(new ProviderIdentity("microsoft", subject), provision.Identity);
    }

    private static IConfiguration Config(params string[] entries)
    {
        var pairs = entries.Select((e, i) => new KeyValuePair<string, string?>($"AdminAllowlist:Subjects:{i}", e));
        return new ConfigurationBuilder().AddInMemoryCollection(pairs).Build();
    }

    /// <summary>Answers NotFound for resolve/bind and a Resolution for self-provision, recording every Send.</summary>
    private sealed class RecordingMediator : AnsweringMediator
    {
        public List<object> Sent { get; } = [];

        protected override object? Answer(object message)
        {
            Sent.Add(message);
            return message is SelfProvisionSuperCommand
                ? new Resolution(Guid.NewGuid(), "ops@org.com", Admins.Domain.Users.Tier.Super, AccessibleMerchants.All)
                : ResolveResult.NotFound;
        }
    }
}
