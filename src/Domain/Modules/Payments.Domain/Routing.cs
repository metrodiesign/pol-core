using SharedKernel;

namespace Payments.Domain.Routing;

public enum RoutingRulesetStatus
{
    Draft = 1,
    PendingApproval = 2,
    Active = 3,
    Superseded = 4,
}

public sealed record RoutingRuleSpec(
    int Priority,
    string Method,
    Guid? OriginatorId,
    decimal? MinAmount,
    decimal? MaxAmount,
    Guid TargetConnectionId,
    Guid? FallbackConnectionId,
    bool Enabled);

public sealed class RoutingRuleset : AggregateRoot<Guid>
{
    private readonly List<RoutingRule> _rules = [];

    public Guid MerchantId { get; private set; }
    public string Name { get; private set; } = default!;
    public RoutingRulesetStatus Status { get; private set; }
    public Guid? ApprovalId { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public long Version { get; private set; }
    public IReadOnlyCollection<RoutingRule> Rules => _rules;

    private RoutingRuleset() { }

    public static RoutingRuleset Create(Guid merchantId, string name, IReadOnlyList<RoutingRuleSpec> rules, DateTime now)
    {
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        var ruleset = new RoutingRuleset
        {
            Id = Guid.CreateVersion7(),
            MerchantId = merchantId,
            Name = Required(name, 200),
            Status = RoutingRulesetStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
        ruleset.ReplaceRules(rules, now, incrementVersion: false);
        return ruleset;
    }

    public void Replace(string name, IReadOnlyList<RoutingRuleSpec> rules, DateTime now)
    {
        if (Status != RoutingRulesetStatus.Draft)
            throw new InvalidOperationException("Only a draft routing ruleset can be replaced.");
        Name = Required(name, 200);
        ReplaceRules(rules, now, incrementVersion: true);
    }

    public void RequestActivation(Guid approvalId, DateTime now)
    {
        if (approvalId == Guid.Empty)
            throw new ArgumentException("ApprovalId is required.", nameof(approvalId));
        if (Status != RoutingRulesetStatus.Draft)
            throw new InvalidOperationException("Only a draft routing ruleset can request activation.");
        ApprovalId = approvalId;
        Status = RoutingRulesetStatus.PendingApproval;
        UpdatedAt = now;
        Version++;
    }

    public void ReturnToDraft(DateTime now)
    {
        if (Status != RoutingRulesetStatus.PendingApproval)
            return;
        Status = RoutingRulesetStatus.Draft;
        ApprovalId = null;
        UpdatedAt = now;
        Version++;
    }

    public void Activate(DateTime now)
    {
        if (Status != RoutingRulesetStatus.PendingApproval)
            throw new InvalidOperationException("Routing ruleset is not pending approval.");
        Status = RoutingRulesetStatus.Active;
        UpdatedAt = now;
        Version++;
    }

    public void Supersede(DateTime now)
    {
        if (Status != RoutingRulesetStatus.Active)
            return;
        Status = RoutingRulesetStatus.Superseded;
        UpdatedAt = now;
        Version++;
    }

    private void ReplaceRules(IReadOnlyList<RoutingRuleSpec> specs, DateTime now, bool incrementVersion)
    {
        ArgumentNullException.ThrowIfNull(specs);
        Validate(specs);
        _rules.Clear();
        foreach (var spec in specs.OrderBy(x => x.Priority))
            _rules.Add(RoutingRule.Create(MerchantId, Id, spec));
        UpdatedAt = now;
        if (incrementVersion)
            Version++;
    }

    public static void Validate(IReadOnlyList<RoutingRuleSpec> specs)
    {
        if (specs.Count == 0)
            throw new ArgumentException("At least one routing rule is required.", nameof(specs));
        if (specs.Select(x => x.Priority).Any(x => x <= 0)
            || specs.Select(x => x.Priority).Distinct().Count() != specs.Count)
            throw new ArgumentException("Routing priorities must be positive and unique.", nameof(specs));

        foreach (var rule in specs)
        {
            var method = NormalizeMethod(rule.Method);
            if (rule.TargetConnectionId == Guid.Empty || rule.FallbackConnectionId == Guid.Empty)
                throw new ArgumentException("Routing connection identifiers cannot be empty.", nameof(specs));
            if (rule.FallbackConnectionId == rule.TargetConnectionId)
                throw new ArgumentException("Routing fallback must differ from target.", nameof(specs));
            if (rule.MinAmount is < 0 || rule.MaxAmount is < 0 || rule.MinAmount > rule.MaxAmount)
                throw new ArgumentException("Routing amount range is invalid.", nameof(specs));

            foreach (var other in specs.Where(x => x.Enabled && rule.Enabled && x.Priority > rule.Priority))
            {
                if (NormalizeMethod(other.Method) != method || other.OriginatorId != rule.OriginatorId)
                    continue;
                if (RangesOverlap(rule.MinAmount, rule.MaxAmount, other.MinAmount, other.MaxAmount))
                    throw new RoutingOverlapException("Enabled routing predicates overlap.");
            }
        }
    }

    internal static string NormalizeMethod(string value)
    {
        var method = Required(value, 30).ToLowerInvariant();
        return method is "any" or "card" or "promptpay" or "installment"
            ? method
            : throw new ArgumentException("Routing method is unsupported.", nameof(value));
    }

    private static bool RangesOverlap(decimal? aMin, decimal? aMax, decimal? bMin, decimal? bMax) =>
        (aMin ?? decimal.MinValue) <= (bMax ?? decimal.MaxValue)
        && (bMin ?? decimal.MinValue) <= (aMax ?? decimal.MaxValue);

    private static string Required(string value, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"Value exceeds {maxLength} characters.", nameof(value));
        return trimmed;
    }
}

public sealed class RoutingRule : Entity<Guid>
{
    public Guid MerchantId { get; private set; }
    public Guid RulesetId { get; private set; }
    public int Priority { get; private set; }
    public string Method { get; private set; } = default!;
    public Guid? OriginatorId { get; private set; }
    public decimal? MinAmount { get; private set; }
    public decimal? MaxAmount { get; private set; }
    public Guid TargetConnectionId { get; private set; }
    public Guid? FallbackConnectionId { get; private set; }
    public bool Enabled { get; private set; }

    private RoutingRule() { }

    internal static RoutingRule Create(Guid merchantId, Guid rulesetId, RoutingRuleSpec spec) => new()
    {
        Id = Guid.CreateVersion7(),
        MerchantId = merchantId,
        RulesetId = rulesetId,
        Priority = spec.Priority,
        Method = RoutingRuleset.NormalizeMethod(spec.Method),
        OriginatorId = spec.OriginatorId,
        MinAmount = spec.MinAmount,
        MaxAmount = spec.MaxAmount,
        TargetConnectionId = spec.TargetConnectionId,
        FallbackConnectionId = spec.FallbackConnectionId,
        Enabled = spec.Enabled,
    };
}

public sealed class RoutingOverlapException(string message) : InvalidOperationException(message);
