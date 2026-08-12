using BuildingBlocks.Infrastructure.Persistence;
using Governance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.ControlPlane.Governance;

// Runtime twins of Governance.Infrastructure mappings. ControlPlane deliberately never references module
// Infrastructure assemblies; migration-owner and runtime model parity is covered by Architecture.Tests.
internal sealed class ApprovalRequestConfiguration : IEntityTypeConfiguration<ApprovalRequest>
{
    public void Configure(EntityTypeBuilder<ApprovalRequest> builder)
    {
        builder.ToTable("ApprovalRequests", SchemaNames.Admin, table => table.HasCheckConstraint(
            "CK_ApprovalRequests_Scope",
            "([ScopeKind] = 1 AND [MerchantId] IS NULL) OR ([ScopeKind] = 2 AND [MerchantId] IS NOT NULL)"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ScopeKind).HasConversion<int>().IsRequired();
        builder.Property(x => x.Action).HasMaxLength(120).IsUnicode(false).IsRequired();
        builder.Property(x => x.RequiredPermission).HasMaxLength(120).IsUnicode(false).IsRequired();
        builder.Property(x => x.TargetType).HasMaxLength(120).IsUnicode(false).IsRequired();
        builder.Property(x => x.TargetId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.TargetVersion).HasMaxLength(200).IsUnicode(false).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.DecisionReason).HasMaxLength(1000);
        builder.Property(x => x.ExecutionOutcome).HasMaxLength(1000);
        builder.Property(x => x.CorrelationId).HasMaxLength(128).IsUnicode(false).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => new { x.Status, x.CreatedAt });
        builder.HasIndex(x => new { x.MerchantId, x.CreatedAt });
        TenantKeyDescriptor.Require(builder.Metadata, nameof(ApprovalRequest.MerchantId), allowNullable: true);
    }
}

internal sealed class ApprovalEventConfiguration : IEntityTypeConfiguration<ApprovalEvent>
{
    public void Configure(EntityTypeBuilder<ApprovalEvent> builder)
    {
        builder.ToTable("ApprovalEvents", SchemaNames.Admin, table => table.HasCheckConstraint(
            "CK_ApprovalEvents_Scope",
            "([ScopeKind] = 1 AND [MerchantId] IS NULL) OR ([ScopeKind] = 2 AND [MerchantId] IS NOT NULL)"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ScopeKind).HasConversion<int>().IsRequired();
        builder.Property(x => x.Kind).HasMaxLength(40).IsUnicode(false).IsRequired();
        builder.Property(x => x.Detail).HasMaxLength(1000);
        builder.Property(x => x.CorrelationId).HasMaxLength(128).IsUnicode(false).IsRequired();
        builder.HasIndex(x => x.SourceEventId).IsUnique();
        builder.HasIndex(x => new { x.ApprovalId, x.OccurredAt });
        builder.HasOne<ApprovalRequest>().WithMany().HasForeignKey(x => x.ApprovalId).OnDelete(DeleteBehavior.Restrict);
        TenantKeyDescriptor.Require(builder.Metadata, nameof(ApprovalEvent.MerchantId), allowNullable: true);
        AppendOnlyDescriptor.Mark(builder.Metadata);
    }
}

internal sealed class OperationRecordConfiguration : IEntityTypeConfiguration<OperationRecord>
{
    public void Configure(EntityTypeBuilder<OperationRecord> builder)
    {
        builder.ToTable("OperationRecords", SchemaNames.Admin, table => table.HasCheckConstraint(
            "CK_OperationRecords_Scope",
            "([ScopeKind] = 1 AND [MerchantId] IS NULL) OR ([ScopeKind] = 2 AND [MerchantId] IS NOT NULL)"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Operation).HasMaxLength(120).IsUnicode(false).IsRequired();
        builder.Property(x => x.IdempotencyKey).HasMaxLength(200).IsUnicode(false).IsRequired();
        builder.Property(x => x.RequestHash).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(x => x.ScopeKind).HasConversion<int>().IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.ResponseBody);
        builder.HasIndex(x => new { x.ActorId, x.Operation, x.IdempotencyKey }).IsUnique();
        builder.HasIndex(x => x.ExpiresAt);
        TenantKeyDescriptor.Require(builder.Metadata, nameof(OperationRecord.MerchantId), allowNullable: true);
    }
}

internal sealed class AuditHeadConfiguration : IEntityTypeConfiguration<AuditHead>
{
    public void Configure(EntityTypeBuilder<AuditHead> builder)
    {
        builder.ToTable("AuditHeads", SchemaNames.Admin, table => table.HasCheckConstraint(
            "CK_AuditHeads_Scope",
            "([ScopeKind] = 1 AND [MerchantId] IS NULL) OR ([ScopeKind] = 2 AND [MerchantId] IS NOT NULL)"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("ScopeKey").HasMaxLength(80).IsUnicode(false);
        builder.Property(x => x.ScopeKind).HasConversion<int>().IsRequired();
        builder.Property(x => x.LastHash).HasColumnType("binary(32)").IsRequired();
        builder.HasIndex(x => new { x.ScopeKind, x.MerchantId }).IsUnique();
        builder.HasIndex(x => x.ScopeKind).IsUnique().HasFilter("[MerchantId] IS NULL")
            .HasDatabaseName("UX_AuditHeads_PlatformScope");
        TenantKeyDescriptor.Require(builder.Metadata, nameof(AuditHead.MerchantId), allowNullable: true);
    }
}

internal sealed class AuditRecordConfiguration : IEntityTypeConfiguration<AuditRecord>
{
    public void Configure(EntityTypeBuilder<AuditRecord> builder)
    {
        builder.ToTable("AuditRecords", SchemaNames.Admin, table => table.HasCheckConstraint(
            "CK_AuditRecords_Scope",
            "([ScopeKind] = 1 AND [MerchantId] IS NULL) OR ([ScopeKind] = 2 AND [MerchantId] IS NOT NULL)"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ScopeKey).HasMaxLength(80).IsUnicode(false).IsRequired();
        builder.Property(x => x.ScopeKind).HasConversion<int>().IsRequired();
        builder.Property(x => x.Action).HasMaxLength(120).IsUnicode(false).IsRequired();
        builder.Property(x => x.ResourceType).HasMaxLength(120).IsUnicode(false).IsRequired();
        builder.Property(x => x.ResourceId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Result).HasMaxLength(80).IsUnicode(false).IsRequired();
        builder.Property(x => x.Changes).IsRequired();
        builder.Property(x => x.ResourceVersion).HasMaxLength(200).IsUnicode(false);
        builder.Property(x => x.CorrelationId).HasMaxLength(128).IsUnicode(false).IsRequired();
        builder.Property(x => x.PreviousHash).HasColumnType("binary(32)").IsRequired();
        builder.Property(x => x.Hash).HasColumnType("binary(32)").IsRequired();
        builder.HasIndex(x => new { x.ScopeKey, x.Sequence }).IsUnique();
        builder.HasIndex(x => new { x.ScopeKey, x.PreviousHash }).IsUnique();
        builder.HasIndex(x => new { x.ActorId, x.OccurredAt });
        builder.HasIndex(x => new { x.Action, x.OccurredAt });
        builder.HasOne<AuditHead>().WithMany().HasForeignKey(x => x.ScopeKey).OnDelete(DeleteBehavior.Restrict);
        TenantKeyDescriptor.Require(builder.Metadata, nameof(AuditRecord.MerchantId), allowNullable: true);
        AppendOnlyDescriptor.Mark(builder.Metadata);
    }
}

internal sealed class GovernanceOutboxMessageConfiguration : IEntityTypeConfiguration<GovernanceOutboxMessage>
{
    public void Configure(EntityTypeBuilder<GovernanceOutboxMessage> builder)
    {
        builder.ToTable("GovernanceOutboxMessages", SchemaNames.Admin, table => table.HasCheckConstraint(
            "CK_GovernanceOutboxMessages_Scope",
            "([ScopeKind] = 1 AND [MerchantId] IS NULL) OR ([ScopeKind] = 2 AND [MerchantId] IS NOT NULL)"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ScopeKind).HasConversion<int>().IsRequired();
        builder.Property(x => x.Type).HasMaxLength(200).IsUnicode(false).IsRequired();
        builder.Property(x => x.SchemaVersion).HasMaxLength(16).IsUnicode(false).IsRequired();
        builder.Property(x => x.Payload).IsRequired();
        builder.Property(x => x.Error).HasMaxLength(1000);
        builder.Property(x => x.LeaseOwner).HasMaxLength(200);
        builder.HasIndex(x => new { x.ProcessedAt, x.LeaseExpiresAt });
        TenantKeyDescriptor.Require(builder.Metadata, nameof(GovernanceOutboxMessage.MerchantId), allowNullable: true);
    }
}
