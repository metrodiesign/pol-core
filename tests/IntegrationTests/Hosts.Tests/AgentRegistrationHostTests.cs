extern alias ApiHost;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Accounts.Domain;
using Admins.Application;
using Admins.Application.Users;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using BuildingBlocks.Infrastructure.Persistence;
using Iam.Domain.Permissions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Persistence.ControlPlane;
using Resolution = Admins.Application.Users.Resolution;
using IntegrationDb = Integration.Tests.IntegrationDb;

namespace Hosts.Tests;

[Collection("IdentityAccessSql")]
[Trait("Capability", "Registration")]
[Trait("Category", "Integration")]
public sealed class AgentRegistrationHostTests
{
    [Fact]
    [Trait("Requirement", "REQ-4.10")]
    [Trait("Requirement", "REQ-4.11")]
    [Trait("Requirement", "REQ-4.12")]
    public async Task Applicant_route_is_identity_scoped_publicly_redacted_and_reviewer_projection_is_gated()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            using var factory = new RegistrationHostFactory(
                IntegrationDb.SaConnFor(fixture.Database), fixture.MerchantA, grantReview: true);
            using var applicant = factory.CreateClient();
            AddRegistrationCookie(applicant, fixture.SessionA);

            var draft = await applicant.PutAsJsonAsync("/api/v1/agent-registration", new
            {
                saleCode = "host-sale",
                email = "old-contact@example.test",
                phoneNumber = "0811111111",
                profile = new { schemaVersion = 1, firstName = "Host", lastName = "Applicant", personType = "Individual", idNumber = "1234567890123", privateField = "must-not-be-public" },
            });
            Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
            using (var draftJson = await JsonDocument.ParseAsync(await draft.Content.ReadAsStreamAsync()))
            {
                // The applicant's own case carries the draft back for prefill (gap 6 of the SPA migration brief).
                var registration = draftJson.RootElement.GetProperty("registration");
                Assert.Equal("host-sale", registration.GetProperty("saleCode").GetString());
                Assert.Equal("old-contact@example.test", registration.GetProperty("email").GetString());
                Assert.Equal("Host", registration.GetProperty("profile").GetProperty("firstName").GetString());
                Assert.False(registration.GetProperty("hasPhoto").GetBoolean());
                Assert.Equal("submit", draftJson.RootElement.GetProperty("nextAction").GetString());
            }

            // Profile contract (schemaVersion 1): firstName/lastName/personType/idNumber are required.
            var invalidProfile = await applicant.PutAsJsonAsync("/api/v1/agent-registration", new
            {
                saleCode = "host-sale",
                email = "old-contact@example.test",
                phoneNumber = "0811111111",
                profile = new { lastName = "Applicant", personType = "Company", idNumber = "1" },
            });
            Assert.Equal(HttpStatusCode.BadRequest, invalidProfile.StatusCode);
            Assert.Contains("validation_failed", await invalidProfile.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            // Submitting without a photo is a coded 400, not a 409/500.
            using var noPhotoSubmit = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent-registration/submissions");
            noPhotoSubmit.Headers.TryAddWithoutValidation("Cookie", $"pol_registration_session={fixture.SessionA}");
            noPhotoSubmit.Headers.TryAddWithoutValidation("If-Match", draft.Headers.ETag!.Tag);
            noPhotoSubmit.Headers.TryAddWithoutValidation("Idempotency-Key", "host-submit-0");
            var noPhoto = await applicant.SendAsync(noPhotoSubmit);
            Assert.Equal(HttpStatusCode.BadRequest, noPhoto.StatusCode);
            Assert.Contains("photo_required", await noPhoto.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            var photos = await applicant.PutAsync("/api/v1/agent-registration/photos", PhotoForm(includeKyc: false));
            Assert.True(photos.StatusCode == HttpStatusCode.OK, await photos.Content.ReadAsStringAsync());
            using (var photosJson = await JsonDocument.ParseAsync(await photos.Content.ReadAsStreamAsync()))
                Assert.True(photosJson.RootElement.GetProperty("registration").GetProperty("hasPhoto").GetBoolean());
            var draftEtag = photos.Headers.ETag!.Tag;
            Assert.NotEqual(draft.Headers.ETag!.Tag, draftEtag);

            using var submitRequest = new HttpRequestMessage(
                HttpMethod.Post, "/api/v1/agent-registration/submissions")
            {
                Content = JsonContent.Create(new { }),
            };
            submitRequest.Headers.TryAddWithoutValidation("Cookie", $"pol_registration_session={fixture.SessionA}");
            submitRequest.Headers.TryAddWithoutValidation("If-Match", draftEtag);
            submitRequest.Headers.TryAddWithoutValidation("Idempotency-Key", "host-submit-1");
            var submit = await applicant.SendAsync(submitRequest);
            Assert.Equal(HttpStatusCode.Created, submit.StatusCode);
            var submitJson = await JsonDocument.ParseAsync(await submit.Content.ReadAsStreamAsync());
            var registrationId = submitJson.RootElement.GetProperty("registration").GetProperty("registrationId").GetGuid();
            var attemptId = submitJson.RootElement.GetProperty("attempt").GetProperty("attemptId").GetGuid();
            var submitEtag = submit.Headers.ETag!.Tag;

            // Editing while an attempt is pending is a coded 409 the SPA can branch on.
            var editWhilePending = await applicant.PutAsJsonAsync("/api/v1/agent-registration", new
            {
                saleCode = "host-sale",
                email = "old-contact@example.test",
                phoneNumber = "0811111111",
                profile = new { schemaVersion = 1, firstName = "Host", lastName = "Applicant", personType = "Individual", idNumber = "1234567890123" },
            });
            Assert.Equal(HttpStatusCode.Conflict, editWhilePending.StatusCode);
            Assert.Contains("registration_pending", await editWhilePending.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            var publicBeforeDecision = await applicant.GetAsync("/api/v1/agent-registration/history");
            Assert.Equal(HttpStatusCode.OK, publicBeforeDecision.StatusCode);
            AssertPublicHistory(await JsonDocument.ParseAsync(await publicBeforeDecision.Content.ReadAsStreamAsync()),
                expectedStatuses: ["Pending"]);

            using var reviewer = factory.CreateClient();
            var rejected = await reviewer.SendAsync(RejectRequest(
                registrationId, attemptId, submitEtag, "public rejection", "private review note"));
            Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);

            await using (var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(fixture.Database)))
            {
                Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection,
                    "SELECT COUNT(*) FROM acct.Accounts WHERE AccountType=2;")));
            }

            var correctedDraft = await applicant.PutAsJsonAsync("/api/v1/agent-registration", new
            {
                saleCode = "host-sale",
                email = "new-contact@example.test",
                phoneNumber = "0822222222",
                profile = new { schemaVersion = 1, firstName = "Corrected", lastName = "Applicant", personType = "Juristic", idNumber = "0105551234567", licenseNumber = "LIC-1", acceptedTermsAt = "2026-09-15T00:00:00Z" },
            });
            Assert.Equal(HttpStatusCode.OK, correctedDraft.StatusCode);
            using var secondSubmitRequest = new HttpRequestMessage(
                HttpMethod.Post, "/api/v1/agent-registration/submissions")
            {
                Content = JsonContent.Create(new { }),
            };
            secondSubmitRequest.Headers.TryAddWithoutValidation("Cookie", $"pol_registration_session={fixture.SessionA}");
            secondSubmitRequest.Headers.TryAddWithoutValidation("If-Match", correctedDraft.Headers.ETag!.Tag);
            secondSubmitRequest.Headers.TryAddWithoutValidation("Idempotency-Key", "host-submit-2");
            var secondSubmit = await applicant.SendAsync(secondSubmitRequest);
            Assert.Equal(HttpStatusCode.Created, secondSubmit.StatusCode);
            var secondJson = await JsonDocument.ParseAsync(await secondSubmit.Content.ReadAsStreamAsync());
            var secondAttemptId = secondJson.RootElement.GetProperty("attempt").GetProperty("attemptId").GetGuid();
            Assert.Equal(2, secondJson.RootElement.GetProperty("attempt").GetProperty("attemptNo").GetInt32());

            var publicPending = await applicant.GetAsync("/api/v1/agent-registration/history");
            AssertPublicHistory(await JsonDocument.ParseAsync(await publicPending.Content.ReadAsStreamAsync()),
                expectedStatuses: ["Rejected", "Pending"]);

            // The reviewer takes the If-Match value from its own read of the case, not from the applicant.
            var reviewerCase = await reviewer.GetAsync($"/api/v1/agent-registrations/{registrationId}");
            Assert.Equal(HttpStatusCode.OK, reviewerCase.StatusCode);
            Assert.Equal(secondSubmit.Headers.ETag!.Tag, reviewerCase.Headers.ETag!.Tag);
            var approved = await reviewer.SendAsync(ApproveRequest(
                registrationId, secondAttemptId, reviewerCase.Headers.ETag.Tag, "official-record"));
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

            var publicApproved = await applicant.GetAsync("/api/v1/agent-registration/history");
            AssertPublicHistory(await JsonDocument.ParseAsync(await publicApproved.Content.ReadAsStreamAsync()),
                expectedStatuses: ["Rejected", "Approved"]);

            var reviewerAttempts = await reviewer.GetAsync($"/api/v1/agent-registrations/{registrationId}/attempts");
            Assert.Equal(HttpStatusCode.OK, reviewerAttempts.StatusCode);
            using var reviewerJson = await JsonDocument.ParseAsync(await reviewerAttempts.Content.ReadAsStreamAsync());
            var firstReviewerAttempt = reviewerJson.RootElement.EnumerateArray().First();
            Assert.Equal("private review note", firstReviewerAttempt.GetProperty("internalReviewNote").GetString());
            Assert.Equal("old-contact@example.test", firstReviewerAttempt.GetProperty("email").GetString());
            Assert.Equal("0811111111", firstReviewerAttempt.GetProperty("phoneNumber").GetString());
            Assert.True(firstReviewerAttempt.TryGetProperty("profileJson", out _));
            Assert.True(firstReviewerAttempt.GetProperty("hasPhoto").GetBoolean());
            Assert.False(firstReviewerAttempt.GetProperty("hasKycPhoto").GetBoolean());

            // The reviewer reads the submitted photo snapshot; the optional KYC photo was never uploaded.
            var reviewerPhoto = await reviewer.GetAsync($"/api/v1/agent-registrations/{registrationId}/attempts/{attemptId}/photos/photo");
            Assert.Equal(HttpStatusCode.OK, reviewerPhoto.StatusCode);
            Assert.Equal("image/png", reviewerPhoto.Content.Headers.ContentType!.MediaType);
            Assert.Equal(OnePixelPng, await reviewerPhoto.Content.ReadAsByteArrayAsync());
            Assert.Equal(HttpStatusCode.NotFound,
                (await reviewer.GetAsync($"/api/v1/agent-registrations/{registrationId}/attempts/{attemptId}/photos/kyc")).StatusCode);

            await using (var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(fixture.Database)))
            {
                var payloads = await ReadPayloadsAsync(connection);
                Assert.Equal(2, payloads.Count);
                Assert.Contains(payloads, payload => payload.Contains("old-contact@example.test", StringComparison.Ordinal)
                    && payload.Contains("0811111111", StringComparison.Ordinal)
                    && !payload.Contains("new-contact@example.test", StringComparison.Ordinal));
                Assert.Contains(payloads, payload => payload.Contains("new-contact@example.test", StringComparison.Ordinal)
                    && payload.Contains("0822222222", StringComparison.Ordinal));
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection,
                    "SELECT COUNT(*) FROM acct.Accounts WHERE AccountType=2;")));
            }

            using var otherApplicant = factory.CreateClient();
            AddRegistrationCookie(otherApplicant, fixture.SessionB);
            Assert.Equal(HttpStatusCode.NotFound,
                (await otherApplicant.GetAsync("/api/v1/agent-registration")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await otherApplicant.GetAsync("/api/v1/agent-registration/history")).StatusCode);

            using var outsideReviewerFactory = new RegistrationHostFactory(
                IntegrationDb.SaConnFor(fixture.Database), fixture.MerchantB, grantReview: true);
            using var outsideReviewer = outsideReviewerFactory.CreateClient();
            Assert.Equal(HttpStatusCode.NotFound,
                (await outsideReviewer.GetAsync($"/api/v1/agent-registrations/{registrationId}/attempts")).StatusCode);

            using var deniedReviewerFactory = new RegistrationHostFactory(
                IntegrationDb.SaConnFor(fixture.Database), fixture.MerchantA, grantReview: false);
            using var deniedReviewer = deniedReviewerFactory.CreateClient();
            Assert.Equal(HttpStatusCode.Forbidden,
                (await deniedReviewer.GetAsync($"/api/v1/agent-registrations/{registrationId}/attempts")).StatusCode);
        }
        finally
        {
            await DropDatabaseAsync(fixture.Database);
        }
    }

    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static MultipartFormDataContent PhotoForm(bool includeKyc)
    {
        var form = new MultipartFormDataContent();
        var photo = new ByteArrayContent(OnePixelPng);
        photo.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(photo, "photo", "photo.png");
        if (includeKyc)
        {
            var kyc = new ByteArrayContent(OnePixelPng);
            kyc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(kyc, "kycPhoto", "kyc.png");
        }
        return form;
    }

    private static void AssertPublicHistory(JsonDocument document, string[] expectedStatuses)
    {
        var attempts = document.RootElement.GetProperty("attempts").EnumerateArray().ToArray();
        Assert.Equal(expectedStatuses, attempts.Select(x => x.GetProperty("status").GetString()).ToArray());
        foreach (var attempt in attempts)
        {
            Assert.False(attempt.TryGetProperty("internalReviewNote", out _));
            Assert.False(attempt.TryGetProperty("decidedByAccountId", out _));
            Assert.False(attempt.TryGetProperty("email", out _));
            Assert.False(attempt.TryGetProperty("phoneNumber", out _));
            Assert.False(attempt.TryGetProperty("profileJson", out _));
        }
        if (expectedStatuses.Contains("Rejected", StringComparer.Ordinal))
            Assert.Equal("public rejection", attempts[0].GetProperty("rejectionReason").GetString());
    }

    private static HttpRequestMessage RejectRequest(
        Guid registrationId, Guid attemptId, string? etag, string reason, string internalNote)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/agent-registrations/{registrationId}/attempts/{attemptId}/reject")
        {
            Content = JsonContent.Create(new { rejectionReason = reason, internalReviewNote = internalNote }),
        };
        AddAdminHeaders(request, etag, "host-reject-1");
        return request;
    }

    private static HttpRequestMessage ApproveRequest(
        Guid registrationId, Guid attemptId, string? etag, string evidence)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/agent-registrations/{registrationId}/attempts/{attemptId}/approve")
        {
            Content = JsonContent.Create(new { contactEvidenceReference = evidence }),
        };
        AddAdminHeaders(request, etag, "host-approve-1");
        return request;
    }

    private static void AddAdminHeaders(HttpRequestMessage request, string? etag, string idempotencyKey)
    {
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
    }

    private static void AddRegistrationCookie(HttpClient client, string token) =>
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", $"pol_registration_session={token}");

    private static async Task<HostFixture> CreateFixtureAsync()
    {
        var database = $"PolRegistrationHost_{Guid.NewGuid():N}";
        try
        {
            await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
            await using (var migration = NewMigrationContext(database))
                await migration.GetService<IMigrator>().MigrateAsync();

            var merchantA = Guid.NewGuid();
            var merchantB = Guid.NewGuid();
            var branch = Guid.NewGuid();
            var sale = Guid.NewGuid();
            await using (var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
            {
                await IntegrationDb.InsertMerchantAsync(connection, merchantA, $"host-a-{Guid.NewGuid():N}"[..24]);
                await IntegrationDb.InsertMerchantAsync(connection, merchantB, $"host-b-{Guid.NewGuid():N}"[..24]);
                await IntegrationDb.ExecAsync(connection, """
                    INSERT merch.Branches (Id, MerchantId, Code, Name, Status, CreatedAt, UpdatedAt, Version)
                    VALUES (@branch, @merchant, N'host-branch', N'Host branch', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 1);
                    INSERT merch.Sales (Id, MerchantId, BranchId, Code, Name, Status, CreatedAt, UpdatedAt, Version)
                    VALUES (@sale, @merchant, @branch, N'host-sale', N'Host sale', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 1);
                    """, ("@branch", branch), ("@merchant", merchantA), ("@sale", sale));
            }

            var identityA = ExternalIdentity.Create("microsoft", "host-tenant", $"host-a-{Guid.NewGuid():N}");
            var identityB = ExternalIdentity.Create("microsoft", "host-tenant", $"host-b-{Guid.NewGuid():N}");
            var rawA = $"host-session-a-{Guid.NewGuid():N}";
            var rawB = $"host-session-b-{Guid.NewGuid():N}";
            await using (var db = NewControlContext(database))
            {
                db.RegistrationSessions.Add(RegistrationSession.Issue(
                    SHA256.HashData(Encoding.UTF8.GetBytes(rawA)), identityA, merchantA,
                    DateTime.UtcNow, TimeSpan.FromMinutes(30)));
                db.RegistrationSessions.Add(RegistrationSession.Issue(
                    SHA256.HashData(Encoding.UTF8.GetBytes(rawB)), identityB, merchantB,
                    DateTime.UtcNow, TimeSpan.FromMinutes(30)));
                await db.SaveChangesAsync();
            }
            return new HostFixture(database, merchantA, merchantB, rawA, rawB);
        }
        catch
        {
            await DropDatabaseAsync(database);
            throw;
        }
    }

    private static async Task<List<string>> ReadPayloadsAsync(Microsoft.Data.SqlClient.SqlConnection connection)
    {
        var payloads = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM admin.GovernanceOutboxMessages WHERE Type=@type ORDER BY OccurredAt";
        command.Parameters.AddWithValue("@type", "AgentRegistrationDecidedV1");
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            payloads.Add(reader.GetString(0));
        return payloads;
    }

    private static PolDbContext NewMigrationContext(string database) => new(
        new DbContextOptionsBuilder<PolDbContext>()
            .UseSqlServer(IntegrationDb.SaConnFor(database), sql => sql.UseCompatibilityLevel(170)).Options,
        new ModuleAssemblies([
            typeof(Products.Infrastructure.ProductsModuleRegistration),
            typeof(Carts.Infrastructure.CartModuleRegistration),
            typeof(Orders.Infrastructure.OrdersModuleRegistration),
            typeof(Payments.Infrastructure.PaymentsModuleRegistration),
            typeof(Merchants.Infrastructure.MerchantsModuleRegistration),
            typeof(Admins.Infrastructure.AdminModuleRegistration),
            typeof(Iam.Infrastructure.IamModuleRegistration),
            typeof(Governance.Infrastructure.GovernanceModuleRegistration),
            typeof(Notifications.Infrastructure.NotificationsModuleRegistration),
            typeof(Accounts.Infrastructure.AccountsModuleRegistration),
            typeof(Access.Infrastructure.AccessModuleRegistration),
        ]));

    private static ControlPlaneDbContext NewControlContext(string database) => new(
        new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseSqlServer(IntegrationDb.SaConnFor(database), sql => sql.UseCompatibilityLevel(170)).Options,
        AllowAll.Instance, NoOpSecurityTelemetry.Instance);

    private static async Task DropDatabaseAsync(string database)
    {
        await using var master = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        await IntegrationDb.ExecAsync(master,
            $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END");
    }

    private sealed record HostFixture(string Database, Guid MerchantA, Guid MerchantB, string SessionA, string SessionB);

    private sealed class AllowAll : IWriteAuthorizer
    {
        public static readonly AllowAll Instance = new();
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }
}

file sealed class RegistrationTestAdminAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "RegistrationTestAdmin";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity([new Claim("sub", "registration-test-admin")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

file sealed class RegistrationTestAdminScope(Guid merchantId, bool grantReview) : IAdminScope
{
    public bool IsBound => true;
    public Resolution Current { get; } = new(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        "registration-reviewer@example.test",
        Admins.Domain.Users.Tier.Scoped,
        AccessibleMerchants.Of(new HashSet<Guid> { merchantId }))
    {
        Permissions = grantReview ? new HashSet<string> { Keys.MerchantUserView, Keys.MerchantUserApprove, Keys.MerchantUserReject } : [],
    };
    public AccessibleMerchants Accessible => Current.Accessible;
}

file sealed class RegistrationHostFactory(string appConnection, Guid merchantId, bool grantReview)
    : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", appConnection);
        builder.UseSetting("ConnectionStrings:Admin", appConnection);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        }));
        builder.ConfigureServices(services =>
        {
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, RegistrationTestAdminAuthHandler>(
                    RegistrationTestAdminAuthHandler.SchemeName, _ => { });
            services.PostConfigure<AuthorizationOptions>(options => options.AddPolicy("admin", policy => policy
                .AddAuthenticationSchemes(RegistrationTestAdminAuthHandler.SchemeName)
                .RequireAuthenticatedUser()));
            services.AddScoped<IAdminScope>(_ => new RegistrationTestAdminScope(merchantId, grantReview));
        });
    }
}
