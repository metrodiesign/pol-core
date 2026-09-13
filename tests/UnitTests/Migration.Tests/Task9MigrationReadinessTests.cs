using Platform.Application.Migration;

namespace Platform.Migration.Tests;

[Trait("Capability", "MigrationReadiness")]
public sealed class Task9MigrationReadinessTests
{
    private static readonly DateTime CapturedAt = new(2026, 9, 10, 22, 0, 0, DateTimeKind.Utc);
    private static readonly Guid RunId = Guid.Parse("90000000-0000-0000-0000-000000000001");
    private static readonly Guid MerchantId = Guid.Parse("90000000-0000-0000-0000-000000000002");
    private static readonly Guid OrderId = Guid.Parse("90000000-0000-0000-0000-000000000003");

    [Fact]
    [Trait("Requirement", "REQ-11.2")]
    public void Identity_mapping_is_deterministic_and_ignores_contact_display_fields()
    {
        var first = Rehearse(CreateInput(humanEmail: "first@example.invalid", humanDisplayName: "First"));
        var second = Rehearse(CreateInput(humanEmail: "changed@example.invalid", humanDisplayName: "Changed"));

        Assert.Equal(first.IdentityMap.Single().AccountId, second.IdentityMap.Single().AccountId);
        Assert.Equal("identity-evidence-1", first.IdentityMap.Single().EvidenceReference);
        Assert.Equal(MigrationHumanTarget.Account, first.Humans.Single().Target);
        Assert.DoesNotContain(first.Conflicts.Conflicts, conflict =>
            conflict.SafeDetails.Contains("first@example", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Requirement", "REQ-11.2")]
    [Trait("Requirement", "REQ-11.4")]
    public void Missing_identity_or_merchant_evidence_blocks_cutover_without_guessing()
    {
        var input = CreateInput(
            humans: [new LegacyHuman(
                new LegacyKey("Human", "legacy-1"),
                LegacyHumanStatus.Active,
                IdentityEvidence: null,
                Guid.Empty,
                null,
                "do-not-use@example.invalid",
                "Do Not Use")]);

        var result = Rehearse(input);

        Assert.True(result.CutoverBlocked);
        Assert.Empty(result.IdentityMap);
        Assert.Empty(result.Humans);
        Assert.Contains(result.Conflicts.Conflicts, x => x.Reason == MigrationConflictReason.MissingIdentityEvidence);
        Assert.Throws<MigrationReadinessBlockedException>(() =>
            MigrationReadinessRunner.EnsureCutoverAllowed(result));
        Assert.All(result.Conflicts.Conflicts, x =>
            Assert.DoesNotContain("do-not-use", x.SafeDetails, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Requirement", "REQ-11.4")]
    public void Duplicate_payment_session_id_has_a_stable_sanitized_conflict()
    {
        var session = LegacySession(Guid.Parse("90000000-0000-0000-0000-000000000010"), OrderId, "req-1", "THB", 100m);
        var duplicate = session with { OrderId = Guid.Parse("90000000-0000-0000-0000-000000000011"), PspRequestReference = "req-2" };
        var first = Rehearse(CreateInput(paymentSessions: [session, duplicate]));
        var second = Rehearse(CreateInput(paymentSessions: [duplicate, session]));

        var conflict = Assert.Single(first.Conflicts.Conflicts);
        Assert.Equal(MigrationConflictReason.PaymentSessionCollision, conflict.Reason);
        Assert.Equal(first.Conflicts.Fingerprint, second.Conflicts.Fingerprint);
        Assert.DoesNotContain("req-", conflict.SafeDetails, StringComparison.Ordinal);
        Assert.Empty(first.Transactions);
        Assert.Throws<MigrationReadinessBlockedException>(() =>
            MigrationReadinessRunner.EnsureCutoverAllowed(first));
    }

    [Fact]
    [Trait("Requirement", "REQ-11.3")]
    [Trait("Requirement", "REQ-11.6")]
    [Trait("Requirement", "REQ-11.7")]
    public void Payment_sessions_become_transactions_without_losing_ids_references_currency_or_history()
    {
        var first = LegacySession(Guid.Parse("90000000-0000-0000-0000-000000000020"), OrderId, "req-thb", "THB", 100m)
            with
            {
                PspTransactionReference = "charge-thb",
                History = [
                    new LegacyPaymentHistory("evt-1", "PENDING", CapturedAt.AddMinutes(-2), null),
                    new LegacyPaymentHistory("evt-2", "SUCCEEDED", CapturedAt.AddMinutes(-1), "charge-thb"),
                ],
            };
        var second = LegacySession(Guid.Parse("90000000-0000-0000-0000-000000000021"), OrderId, "req-usd", "usd", 12.50m);

        var capture = new MigrationSideEffectCapture();
        var result = new MigrationReadinessRunner().Rehearse(
            CreateInput(paymentSessions: [first, second]), CapturedAt, capture);

        Assert.Equal(MigrationRehearsalStatus.Passed, result.Status);
        Assert.True(result.Invariants.Passed);
        Assert.Equal([first.Id, second.Id], result.Transactions.Select(x => x.Id));
        Assert.Equal(["req-thb", "req-usd"], result.Transactions.Select(x => x.PspRequestReference));
        Assert.Equal("THB", result.Transactions[0].Currency);
        Assert.Equal("USD", result.Transactions[1].Currency);
        Assert.Equal(2, result.Transactions[0].History.Count);
        Assert.Equal("MIGRATION_BACKFILL", result.Transactions[0].Snapshot.Provenance);
        Assert.Equal("source-snapshot-1", result.Transactions[0].Snapshot.SourceSnapshotId);
        Assert.Equal(0, result.ExternalCallCount);
        Assert.Equal(0, capture.PspChargeCalls);
        Assert.Equal(0, capture.BusinessEventCalls);
        Assert.Equal(0, capture.EmailCalls + capture.SmsCalls);
    }

    [Fact]
    [Trait("Requirement", "REQ-11.2")]
    [Trait("Requirement", "REQ-11.4")]
    public void Conflicting_provider_identity_evidence_is_not_resolved_by_email_or_display_name()
    {
        var key = new LegacyKey("Human", "conflict-1");
        var humans = new[]
        {
            new LegacyHuman(key, LegacyHumanStatus.Active,
                new IdentityEvidence("entra", "tenant", "subject-a", "evidence-a"), MerchantId, null,
                "same@example.invalid", "Same Name"),
            new LegacyHuman(key, LegacyHumanStatus.Active,
                new IdentityEvidence("entra", "tenant", "subject-b", "evidence-b"), MerchantId, null,
                "same@example.invalid", "Same Name"),
        };

        var result = Rehearse(CreateInput(humans: humans));

        Assert.True(result.CutoverBlocked);
        Assert.Contains(result.Conflicts.Conflicts,
            x => x.Reason == MigrationConflictReason.ConflictingIdentityEvidence);
        Assert.Empty(result.IdentityMap);
        Assert.DoesNotContain(result.Conflicts.Conflicts, x => x.SafeDetails.Contains("same@example", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Requirement", "REQ-11.5")]
    [Trait("Requirement", "REQ-11.6")]
    public void Pending_and_rejected_humans_become_registration_attempts_and_sessions_are_revoked()
    {
        var pending = new LegacyHuman(
            new LegacyKey("Human", "pending-1"), LegacyHumanStatus.Pending,
            new IdentityEvidence("entra", "tenant", "pending-sub", "evidence-pending"),
            MerchantId, Guid.Parse("90000000-0000-0000-0000-000000000041"), "pending@example.invalid", "Pending",
            "+66800000001", "SALE-PENDING", Guid.Parse("90000000-0000-0000-0000-000000000042"));
        var rejected = new LegacyHuman(
            new LegacyKey("Human", "rejected-1"), LegacyHumanStatus.Rejected,
            null, MerchantId, Guid.Parse("90000000-0000-0000-0000-000000000043"), "rejected@example.invalid", "Rejected",
            "+66800000002", "SALE-REJECTED", Guid.Parse("90000000-0000-0000-0000-000000000044"));
        var input = CreateInput(
            humans: [pending, rejected],
            humanSessions: [new(pending.Key, "session-1", CapturedAt)]);

        var result = Rehearse(input);

        Assert.All(result.Humans, human =>
        {
            Assert.Equal(MigrationHumanTarget.RegistrationAttempt, human.Target);
            Assert.Null(human.AccountId);
            Assert.NotNull(human.AttemptId);
            Assert.True(human.RequiresFreshLogin);
        });
        Assert.DoesNotContain(result.IdentityMap, x => x.LegacyId is "pending-1" or "rejected-1");
        var session = Assert.Single(result.Sessions);
        Assert.True(session.Revoked);
        Assert.False(session.RefreshTokenTransferred);
    }

    [Fact]
    [Trait("Requirement", "REQ-11.8")]
    [Trait("Requirement", "REQ-11.9")]
    public void Maintenance_window_allows_one_writer_replays_callbacks_after_watermark_and_rolls_forward()
    {
        var coordinator = new MigrationMaintenanceCoordinator();
        using var writer = coordinator.AcquireWriter(RunId, "task9-writer");
        Assert.Throws<MigrationMaintenanceException>(() => coordinator.AcquireWriter(RunId, "second-writer"));

        var pause = coordinator.Pause(writer);
        var callback = new LegacyCallback("callback-1", "charge-1", CapturedAt, "{\"status\":\"succeeded\"}", 1);
        var stored = coordinator.ReceiveCallback(pause, callback);
        Assert.True(stored.Sequence > pause.Watermark);

        var replayed = coordinator.ReplayAfterWatermark(pause, CapturedAt.AddMinutes(1));
        Assert.Single(replayed);
        Assert.Empty(coordinator.ReplayAfterWatermark(pause, CapturedAt.AddMinutes(2)));

        var rollback = coordinator.RollbackForward(
            pause,
            new ForwardRollbackInput(
                new Dictionary<Guid, string> { [OrderId] = "PAID" },
                ["transaction-succeeded", "business-event"],
                pause.Watermark),
            CapturedAt.AddMinutes(3));

        Assert.True(rollback.TargetPreserved);
        Assert.False(rollback.BackupRestored);
        Assert.Equal("PAID", rollback.PreservedResults[OrderId]);
        Assert.Equal(["transaction-succeeded", "business-event"], rollback.PreservedEvents);
        Assert.Equal(MigrationMaintenanceState.RolledBack, coordinator.State);
    }

    private static MigrationRehearsalResult Rehearse(LegacyMigrationInput input) =>
        new MigrationReadinessRunner().Rehearse(input, CapturedAt);

    private static LegacyMigrationInput CreateInput(
        string humanEmail = "first@example.invalid",
        string humanDisplayName = "First",
        IdentityEvidence? identityEvidence = null,
        Guid? merchantId = null,
        IReadOnlyList<LegacyHuman>? humans = null,
        IReadOnlyList<LegacyHumanSession>? humanSessions = null,
        IReadOnlyList<LegacyPaymentSession>? paymentSessions = null) =>
        LegacyMigrationInput.Create(
            RunId,
            "source-snapshot-1",
            humans ?? [new(
                new LegacyKey("Human", "legacy-1"),
                LegacyHumanStatus.Active,
                identityEvidence ?? new IdentityEvidence("entra", "tenant", "subject-1", "identity-evidence-1"),
                merchantId ?? MerchantId,
                null,
                humanEmail,
                humanDisplayName)],
            humanSessions ?? [],
            paymentSessions ?? [LegacySession(Guid.Parse("90000000-0000-0000-0000-000000000030"), OrderId, "req-default", "THB", 1m)]);

    private static LegacyPaymentSession LegacySession(
        Guid id,
        Guid orderId,
        string requestReference,
        string currency,
        decimal amount) =>
        new(
            id,
            orderId,
            MerchantId,
            "provider-account-1",
            "SANDBOX",
            requestReference,
            null,
            amount,
            currency,
            "PENDING_CONFIRMATION",
            [],
            "{\"legacy\":true}");
}
