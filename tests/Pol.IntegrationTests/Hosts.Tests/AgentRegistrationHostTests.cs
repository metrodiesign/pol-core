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
                profile = new { displayName = "Host Applicant", privateField = "must-not-be-public" },
            });
            Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
            var draftEtag = draft.Headers.ETag!.Tag;

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

            var publicBeforeDecision = await applicant.GetAsync("/api/v1/agent-registration/history");
            Assert.Equal(HttpStatusCode.OK, publicBeforeDecision.StatusCode);
            AssertPublicHistory(await JsonDocument.ParseAsync(await publicBeforeDecision.Content.ReadAsStreamAsync()),
                expectedStatuses: ["PENDING"]);

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
                profile = new { displayName = "Corrected Applicant", privateField = "edited-private" },
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
                expectedStatuses: ["REJECTED", "PENDING"]);

            var approved = await reviewer.SendAsync(ApproveRequest(
                registrationId, secondAttemptId, secondSubmit.Headers.ETag!.Tag, "official-record"));
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

            var publicApproved = await applicant.GetAsync("/api/v1/agent-registration/history");
            AssertPublicHistory(await JsonDocument.ParseAsync(await publicApproved.Content.ReadAsStreamAsync()),
                expectedStatuses: ["REJECTED", "APPROVED"]);

            var reviewerAttempts = await reviewer.GetAsync($"/api/v1/agent-registrations/{registrationId}/attempts");
            Assert.Equal(HttpStatusCode.OK, reviewerAttempts.StatusCode);
            using var reviewerJson = await JsonDocument.ParseAsync(await reviewerAttempts.Content.ReadAsStreamAsync());
            var firstReviewerAttempt = reviewerJson.RootElement.EnumerateArray().First();
            Assert.Equal("private review note", firstReviewerAttempt.GetProperty("internalReviewNote").GetString());
            Assert.Equal("old-contact@example.test", firstReviewerAttempt.GetProperty("email").GetString());
            Assert.Equal("0811111111", firstReviewerAttempt.GetProperty("phoneNumber").GetString());
            Assert.True(firstReviewerAttempt.TryGetProperty("profileJson", out _));

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
        if (expectedStatuses.Contains("REJECTED", StringComparer.Ordinal))
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
        const string csrf = "host-registration-csrf";
        var csrfCookieName = ApiHost::Api.Admins.SessionCookies.CsrfCookieName;
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        request.Headers.TryAddWithoutValidation("Cookie", $"{csrfCookieName}={csrf}");
        request.Headers.TryAddWithoutValidation(ApiHost::Api.Admins.CsrfFilter.HeaderName, csrf);
    }

    private static void AddRegistrationCookie(HttpClient client, string token) =>
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", $"pol_registration_session={token}");

    private static async Task<HostFixture> CreateFixtureAsync()
    {
        var database = $"PolRegistrationHost_{Guid.NewGuid():N}";
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        try
        {
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
            $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
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
