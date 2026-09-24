using CyberCloud.Authorization.Contracts;
using CyberCloud.Billing.Pricing;
using CyberCloud.Tenancy.Contracts;
using Orleans.Multitenant;
using Orleans.Concurrency;
using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.Billing.Grains;

/// <summary>
///     <see cref="ICostQueryGrain" /> — Stateless, key <c>sub/{subscriptionId:N}</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Read <see cref="ICostQueryGrain" /> first</b> for the visibility rule. This class is
///         the order it is applied in: price the scope, ask the subscription, and only when the
///         subscription says no, ask each resource group and then each resource the answer contains.
///     </para>
///     <para>
///         ⚠ <b>The ReBAC object ids are spelled here a second time</b> — <c>{subscription:N}</c>,
///         <c>{subscription:N}-{group}</c> and <c>{resource:N}</c> — because the spelling that
///         writes them lives in <c>CyberCloud.ResourceManager</c>'s <c>ReBacResourceAuthorizer</c>, an
///         implementation assembly this module may not reference. <c>CostVisibilityTests</c> grants a
///         reader through the resource manager's own spelling and holds the two together.
///     </para>
///     <para>
///         <b>Stateless and reentrant-free.</b> The grain holds nothing; it exists so the gateway — an
///         Orleans client — can reach the ledger and the check grains in one hop, and
///         <see cref="StatelessWorkerAttribute" /> lets a busy subscription's portal fan out across
///         activations.
///     </para>
/// </remarks>
[StatelessWorker]
public sealed class CostQueryGrain(IGrainFactory grains, UsagePricing pricing) : Grain, ICostQueryGrain {
    Guid tenantId;
    Guid subscriptionId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = BillingGrainKeys.TenantOf(this);
        subscriptionId = BillingGrainKeys.Decode(this, GrainKeyKind.Subscription).Id;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<CostQueryResult>> QueryAsync(CostQueryRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        var scope = string.IsNullOrEmpty(request.ResourceGroup)
            ? ScopeId.Subscription(tenantId, subscriptionId)
            : ScopeId.Group(tenantId, subscriptionId, request.ResourceGroup);

        if (request.SubscriptionId != subscriptionId) {
            return Result<CostQueryResult>.Failure(
                ErrorCode.InternalError,
                $"The cost query for subscription {request.SubscriptionId:D} reached the grain of {subscriptionId:D}. "
                + "ICostQuery addresses the grain by the request's subscription; this is a routing bug."
            );
        }

        var shaped = Shape(request);
        if (shaped.TryGetError(out var invalid)) {
            return Result<CostQueryResult>.Failure(invalid);
        }

        var (from, to) = shaped.GetValueOrThrow();

        var subject = SubjectRef.Create(request.Caller.SubjectType, request.Caller.SubjectId);
        if (subject.TryGetError(out _)) {
            return NotFound(scope);
        }

        var caller = subject.GetValueOrThrow();

        // ── 1. The subscription, which answers everything when it answers yes. ─────────────────
        var wholeSubscription = await MayReadAsync(ObjectTypes.Subscription, SubscriptionObjectId(), caller);

        // ── 2. The priced usage in scope. ─────────────────────────────────────────────────────
        //
        // ⚠ BEFORE THE NO IS KNOWN, AND A STRANGER PAYS FOR THE READ. A caller who may read nothing still
        // makes this grain read the ledger and rate up to CostQueryRequest.MaxDays before the 404. It
        // can't be decided earlier: a reader of one resource is found only by the resource ids the rows
        // carry, since no list of a subscription's resources exists here, and ListObjects' reverse index
        // may miss (IListObjectsGrain's remarks), which would turn that reader's answer into a 404. The
        // bound is the one every caller has: one ledger read, one period cap.
        var rated = await pricing.RateAsync(tenantId, subscriptionId, from, to);
        if (rated.TryGetError(out var rateError)) {
            return Result<CostQueryResult>.Failure(rateError);
        }

        var inScope = rated.GetValueOrThrow()
            .Where(x => scope.Kind == ScopeKind.Subscription
                || string.Equals(x.ResourceGroup, request.ResourceGroup, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // ── 3. Groups, then resources, when the subscription said no. ─────────────────────────
        var visible = inScope;
        var mayReadSomething = wholeSubscription;
        var mayReadWholeScope = wholeSubscription;

        if (!wholeSubscription) {
            var readableGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var group in await GroupsInScopeAsync(scope, inScope)) {
                if (await MayReadAsync(ObjectTypes.ResourceGroup, GroupObjectId(group), caller)) {
                    readableGroups.Add(group);
                }
            }

            var readableResources = new HashSet<Guid>();

            foreach (var resource in inScope.Where(x => !readableGroups.Contains(x.ResourceGroup))
                         .Select(static x => x.ResourceId)
                         .Distinct()) {
                if (await MayReadAsync(ObjectTypes.Resource, resource.ToString("N", CultureInfo.InvariantCulture), caller)) {
                    readableResources.Add(resource);
                }
            }

            visible = [
                .. inScope.Where(x => readableGroups.Contains(x.ResourceGroup) || readableResources.Contains(x.ResourceId))
            ];
            mayReadSomething = readableGroups.Count > 0 || readableResources.Count > 0;
            mayReadWholeScope = scope.Kind == ScopeKind.ResourceGroup && readableGroups.Contains(request.ResourceGroup);
        }

        // ⚠ The same answer an absent scope gets. A caller who can see nothing here learns nothing —
        // not that the subscription exists, not that it has usage.
        if (!mayReadSomething) {
            return NotFound(scope);
        }

        // ⚠ FILTERED IS ABOUT THE CALLER, NOT ABOUT THE USAGE. It first said whether any row was
        // withheld, which told a reader of one group whether the others had usage in the period—ask
        // day by day and it drew their activity. It now says whether the caller's access covers less
        // than the scope, which is the same answer whatever anyone else used, at either granularity.
        return Answer(request.Grouping, request.Granularity, from, to, visible, !mayReadWholeScope);
    }

    // ── The answer ───────────────────────────────────────────────────────────────────────────────

    Result<CostQueryResult> Answer(
        CostGrouping grouping,
        CostGranularity granularity,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyCollection<RatedHour> visible,
        bool filtered
    ) {
        var currencies = visible.Select(static x => x.Currency).Distinct(StringComparer.Ordinal).ToList();
        if (currencies.Count > 1) {
            return Result<CostQueryResult>.Failure(
                ErrorCode.Conflict,
                $"The usage in scope is priced in {string.Join(" and ", currencies)}. The platform does not convert "
                + "currencies, and a total across two would be a number in neither."
            );
        }

        var currency = currencies.Count == 1 ? currencies[0] : DefaultCurrency();

        var daily = granularity == CostGranularity.Daily;

        // ⚠ Each (day, key) row is rounded on its own, like every row, and the total stays the unrounded
        // sum rounded once — MoneyRounding, rule 6 — so a chart's stacked days may differ from the total
        // by a cent per row, which is what a cost view is allowed and an invoice is not.
        var rows = visible
            .GroupBy(x => (Day: daily ? DayOf(x) : string.Empty, Key: KeyOf(grouping, x)))
            .Select(x => new CostRow {
                    Name = x.Key.Key,
                    Day = x.Key.Day,
                    Amount = MoneyRounding.Round(x.Sum(static y => y.Amount), currency).GetValueOrThrow(),
                    Quantity = grouping == CostGrouping.Meter ? x.Sum(static y => y.Quantity) : 0m
                }
            )
            .OrderBy(static x => x.Day, StringComparer.Ordinal)
            .ThenByDescending(static x => x.Amount)
            .ThenBy(static x => x.Name, StringComparer.Ordinal)
            .ToImmutableArray();

        return Result<CostQueryResult>.Success(
            new() {
                Currency = currency,
                From = from,
                To = to,
                Grouping = grouping,
                Granularity = granularity,
                Rows = rows,
                Total = MoneyRounding.Round(visible.Sum(static x => x.Amount), currency).GetValueOrThrow(),
                Filtered = filtered
            }
        );
    }

    static string KeyOf(CostGrouping grouping, RatedHour hour) =>
        grouping switch {
            CostGrouping.Resource => hour.ResourcePath,
            CostGrouping.ResourceGroup => hour.ResourceGroup,
            CostGrouping.ResourceType => hour.ResourceType,
            CostGrouping.Meter => hour.Meter.ToString(),
            _ => DayOf(hour)
        };

    static string DayOf(RatedHour hour) => hour.WindowStart.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    string DefaultCurrency() =>
        pricing.Sheet.Versions[^1].Meters.Values.Select(static x => x.Currency).FirstOrDefault() ?? Currencies.Euro;

    // ── The question's shape ─────────────────────────────────────────────────────────────────────

    static Result<(DateTimeOffset From, DateTimeOffset To)> Shape(CostQueryRequest request) {
        if (!Enum.IsDefined(request.Granularity)) {
            return Invalid("A cost query's granularity is none or daily.", "/granularity");
        }

        if (request.Granularity == CostGranularity.Daily && request.Grouping == CostGrouping.Day) {
            return Invalid("Grouping by day is already one row per day; daily granularity splits another grouping by day.", "/granularity");
        }

        if (request.Grouping == CostGrouping.Unknown || !Enum.IsDefined(request.Grouping)) {
            return Invalid("A cost query groups by resource, resourceGroup, resourceType, meter or day.", "/groupBy");
        }

        // Usage is hourly, so the period is widened to whole hours: the start down, the end up.
        var from = Hour(request.From.ToUniversalTime());
        var to = Hour(request.To.ToUniversalTime());
        if (to < request.To.ToUniversalTime()) {
            to = to.AddHours(1);
        }

        if (to <= from) {
            return Invalid(
                string.Create(CultureInfo.InvariantCulture, $"The period [{request.From:O}, {request.To:O}) is empty. 'to' is exclusive and later than 'from'."),
                "/to"
            );
        }

        if ((to - from).TotalDays > CostQueryRequest.MaxDays) {
            return Invalid(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The period spans {(to - from).TotalDays:0} days and a query spans at most {CostQueryRequest.MaxDays}. Ask for it a year at a time."
                ),
                "/to"
            );
        }

        return Result<(DateTimeOffset, DateTimeOffset)>.Success((from, to));

        static DateTimeOffset Hour(DateTimeOffset instant) =>
            new(instant.Year, instant.Month, instant.Day, instant.Hour, 0, 0, TimeSpan.Zero);

        static Result<(DateTimeOffset, DateTimeOffset)> Invalid(string message, string target) =>
            Result<(DateTimeOffset, DateTimeOffset)>.Failure(ErrorCode.InvalidRequestBody, message, target);
    }

    // ── ReBAC ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Every group the question could have rows for — the scope's one group, or the subscription's
    ///     groups plus any a row names — so that a caller who may read a group with no usage in the
    ///     period gets an empty answer and not a 404.
    /// </summary>
    async Task<IReadOnlyCollection<string>> GroupsInScopeAsync(ScopeId scope, IEnumerable<RatedHour> rows) {
        if (scope.Kind == ScopeKind.ResourceGroup) {
            return [scope.ResourceGroup];
        }

        var groups = new HashSet<string>(rows.Select(static x => x.ResourceGroup).Where(static x => x.Length > 0), StringComparer.OrdinalIgnoreCase);

        var listed = await Tenant().GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(subscriptionId)).ListResourceGroupsAsync();
        if (listed.TryGetValue(out var names)) {
            groups.UnionWith(names);
        }

        return groups;
    }

    /// <summary>One <c>read</c> check. A check that could not be answered is a no — docs/plan/07 § The enforcement seam.</summary>
    async Task<bool> MayReadAsync(string objectType, string objectId, SubjectRef subject) {
        var checkedRead = await Tenant()
            .GetGrain<ICheckGrain>(GrainKeys.CheckCache(objectType, objectId))
            .CheckAsync(Permissions.Read, subject, Consistency.MinimizeLatency);

        return checkedRead.TryGetValue(out var answer) && answer.Allowed;
    }

    string SubscriptionObjectId() => subscriptionId.ToString("N", CultureInfo.InvariantCulture);

    string GroupObjectId(string group) => SubscriptionObjectId() + "-" + group;

    TenantGrainFactory Tenant() => grains.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture));

    static Result<CostQueryResult> NotFound(ScopeId scope) =>
        Result<CostQueryResult>.Failure(ErrorCode.ResourceNotFound, $"'{scope.Path}' does not exist.");
}
