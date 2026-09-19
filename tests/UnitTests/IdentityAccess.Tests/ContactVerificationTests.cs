using System.Security.Cryptography;
using System.Text;
using Accounts.Application;
using Accounts.Domain;
using BuildingBlocks.Application;

namespace IdentityAccess.Tests;

[Trait("Capability", "IdentityAccess")]
[Trait("Requirement", "Issue-274")]
public sealed class ContactVerificationTests
{
    private static readonly DateTime Now = new(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("0612345678")]
    [InlineData("0812345678")]
    [InlineData("0912345678")]
    [Trait("Requirement", "AC-PHONE-1")]
    [Trait("Requirement", "AC-PHONE-2")]
    public void Canonical_phone_is_preserved(string phone)
    {
        Assert.Equal(phone, AgentRegistration.NormalizePhone(phone));
        Assert.Equal(phone, Create(phone).PhoneNumber);
        AgentRegistrationService.ValidatePhone(phone);
    }

    [Theory]
    [InlineData("+66812345678")]
    [InlineData("66812345678")]
    [InlineData("081-234-5678")]
    [InlineData("081 234 5678")]
    [InlineData("0212345678")]
    [InlineData("0712345678")]
    [InlineData("081234567")]
    [InlineData("08123456789")]
    [InlineData(" 0812345678")]
    [InlineData("0812345678 ")]
    [InlineData("0812345678\n")]
    [InlineData("๐๘๑๒๓๔๕๖๗๘")]
    [InlineData("")]
    [InlineData(null)]
    [Trait("Requirement", "AC-PHONE-3")]
    [Trait("Requirement", "AC-PHONE-4")]
    public void Noncanonical_phone_is_rejected_at_domain_and_application(string? phone)
    {
        Assert.Throws<ArgumentException>(() => AgentRegistration.NormalizePhone(phone!));
        Assert.Throws<ArgumentException>(() => Create(phone!));
        var exception = Assert.Throws<InvalidRequestException>(() => AgentRegistrationService.ValidatePhone(phone!));
        Assert.Equal("phone_invalid", exception.Code);
        var registration = Create();
        Assert.Throws<ArgumentException>(() => registration.UpdateDraft("changed", "changed@example.test", phone!, "{}", Now));
        Assert.Equal("SALE", registration.SaleCode);
    }

    [Fact]
    [Trait("Requirement", "AC-PHONE-5")]
    [Trait("Requirement", "AC-PHONE-6")]
    public void Verification_survives_unchanged_phone_but_resets_after_change()
    {
        var registration = Create();
        registration.MarkPhoneVerified("0812345678", Now);
        registration.UpdateDraft("SALE", "changed@example.test", "0812345678", "{}", Now.AddSeconds(1));
        Assert.True(registration.PhoneVerified);
        Assert.Equal(Now, registration.PhoneVerifiedAt);
        registration.UpdateDraft("SALE", "changed@example.test", "0899999999", "{}", Now.AddSeconds(2));
        Assert.False(registration.PhoneVerified);
        Assert.Null(registration.PhoneVerifiedNumber);
        Assert.Null(registration.PhoneVerifiedAt);
        Assert.Throws<InvalidOperationException>(() => registration.MarkPhoneVerified("0812345678", Now));
    }

    [Fact]
    public void First_bind_clears_unverified_draft_and_rebinding_is_idempotent()
    {
        var registration = Create();
        registration.SetPhotos("photo", "image/jpeg", "kyc", "image/jpeg", Now);
        var identity = ExternalIdentity.Create("microsoft", "tenant", "person");
        registration.BindIdentity(identity, Now);
        Assert.Equal(identity, registration.Identity);
        Assert.Equal("email@example.test", registration.EmailNormalized);
        Assert.Equal("{}", registration.ProfileJson);
        Assert.Equal(string.Empty, registration.PhoneNumber);
        Assert.Equal(string.Empty, registration.SaleCode);
        Assert.Null(registration.PhotoObjectKey);
        Assert.Null(registration.KycPhotoObjectKey);
        var version = registration.Version;
        registration.BindIdentity(identity, Now.AddSeconds(1));
        Assert.Equal(version, registration.Version);
        Assert.Throws<InvalidOperationException>(() => registration.BindIdentity(identity with { ExternalUserId = "other" }, Now));
    }

    [Fact]
    public void Binding_verified_draft_preserves_profile_and_photos()
    {
        var registration = Create();
        registration.SetPhotos("photo", "image/jpeg", null, null, Now);
        registration.MarkPhoneVerified(registration.PhoneNumber, Now);
        registration.BindIdentity(ExternalIdentity.Create("microsoft", "tenant", "person"), Now);
        Assert.True(registration.PhoneVerified);
        Assert.Equal("photo", registration.PhotoObjectKey);
        Assert.Equal("SALE", registration.SaleCode);
    }

    [Fact]
    public void Anonymous_session_requires_a_pinned_registration()
    {
        Assert.Throws<ArgumentException>(() => RegistrationSession.Issue(new byte[32], null, Guid.NewGuid(), Now, TimeSpan.FromMinutes(30)));
        var registration = Create();
        var session = RegistrationSession.Issue(new byte[32], null, registration.MerchantId, Now, TimeSpan.FromMinutes(30), registration.Id);
        Assert.Null(session.Identity);
        Assert.Equal(registration.Id, session.RegistrationId);
        Assert.True(session.IsLiveAt(Now));
        Assert.False(session.IsLiveAt(Now.AddMinutes(30)));
    }

    [Fact]
    public void Challenge_hash_is_bound_to_id_and_confirmation_is_idempotent()
    {
        var (verification, code) = ContactVerification.Issue(Guid.NewGuid(), "0812345678", Now);
        Assert.Matches("^[0-9]{6}$", code);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes($"{verification.Id:N}:{code}")), verification.CodeHash);
        Assert.Equal(ContactVerificationOutcome.Confirmed, verification.TryConfirm(code, Now));
        Assert.Equal(Now, verification.ConfirmedAt);
        Assert.Equal(ContactVerificationOutcome.AlreadyConfirmed, verification.TryConfirm(code, Now.AddHours(1)));
        Assert.Equal(1, verification.Attempts);
    }

    [Fact]
    public void Fifth_failed_attempt_exhausts_challenge_and_expiry_is_exact()
    {
        var (verification, code) = ContactVerification.Issue(Guid.NewGuid(), "0812345678", Now);
        var wrong = code == "000000" ? "000001" : "000000";
        for (var i = 0; i < 5; i++)
            Assert.Equal(ContactVerificationOutcome.Invalid, verification.TryConfirm(wrong, Now));
        Assert.Equal(5, verification.Attempts);
        Assert.Equal(ContactVerificationOutcome.AttemptsExceeded, verification.TryConfirm(code, Now));
        Assert.Null(verification.ConfirmedAt);
        var (expired, expiredCode) = ContactVerification.Issue(Guid.NewGuid(), "0812345678", Now);
        Assert.Equal(ContactVerificationOutcome.Expired, expired.TryConfirm(expiredCode, Now.AddMinutes(5)));
        Assert.Equal(0, expired.Attempts);
    }

    private static AgentRegistration Create(string phone = "0812345678") =>
        AgentRegistration.Create(Guid.NewGuid(), null, "SALE", " Email@Example.Test ", phone, "{}", Now);
}
