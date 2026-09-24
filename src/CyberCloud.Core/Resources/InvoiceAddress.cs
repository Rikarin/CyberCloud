namespace CyberCloud.Core.Resources;

/// <summary>
///     A tenant's invoices — <c>/tenants/{t}/providers/CyberCloud.CostManagement/invoices</c> — or one
///     of them, by the number printed on it. docs/plan/22 § What is owed, <c>billing-http-surface</c>,
///     issue #41.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Under the cost query's reserved namespace, and on the tenant alone.</b> An invoice is
///         the billing account's, and the account is one per tenant (<c>IBillingAccountGrain</c>'s
///         remarks), so a subscription or a group has no invoices of its own to address — the reverse
///         of <see cref="CostQueryAddress" />, which is refused on a tenant because usage is recorded
///         per subscription. The two grammars are therefore disjoint by scope as well as by type
///         segment, and the router asks this one first only so that the cost query keeps the
///         namespace's catch-all refusal.
///     </para>
///     <para>
///         ⚠ <b>Not under <c>CyberCloud.Billing</c></b>, for the reason <see cref="CostQueryAddress" />
///         gives: that namespace is a provider's, and <c>CyberCloud.Billing/budgets</c> is published in
///         it. Reusing the reserved namespace also means no second reservation in
///         <c>ProviderRegistry.Build</c>.
///     </para>
///     <para>
///         <b>A number is matched as printed</b> — <c>CC-INV-00000042</c> — and nothing else about it is
///         parsed here: the prefix is the issuer's configuration, so the one question this type can
///         answer is whether the segment could be a number at all.
///     </para>
/// </remarks>
/// <param name="TenantId">The tenant whose billing account issued the invoices.</param>
/// <param name="Number">One invoice's number, or empty for the collection.</param>
public readonly record struct InvoiceAddress(Guid TenantId, string Number) {
    /// <summary>The type segment, <c>invoices</c>.</summary>
    public const string TypeSegment = "invoices";

    /// <summary>The three segments after the tenant: <c>/providers/CyberCloud.CostManagement/invoices</c>.</summary>
    public const string Suffix = CostQueryAddress.NamespaceSegment + TypeSegment;

    /// <summary>The longest number this address accepts.</summary>
    public const int MaxNumberLength = 64;

    /// <summary>Whether this address names the collection rather than one invoice.</summary>
    public bool IsCollection => string.IsNullOrEmpty(Number);

    /// <summary>The address, spelled from the tenant.</summary>
    public string Path =>
        ScopeId.Tenant(TenantId).Path + Suffix + (IsCollection ? string.Empty : "/" + Uri.EscapeDataString(Number));

    /// <summary>Recognizes the address.</summary>
    /// <param name="path">A path already known to be under <see cref="CostQueryAddress.NamespaceSegment" />.</param>
    /// <param name="address">The address, when the path is one.</param>
    /// <returns>
    ///     <c>true</c> if <paramref name="path" /> is a tenant followed by <see cref="Suffix" /> and at most
    ///     one segment that could be an invoice number — letters, digits and hyphens, at most
    ///     <see cref="MaxNumberLength" />.
    /// </returns>
    public static bool TryParsePath(string? path, out InvoiceAddress address) {
        address = default;

        if (string.IsNullOrEmpty(path)) {
            return false;
        }

        var at = path.IndexOf(Suffix, StringComparison.OrdinalIgnoreCase);
        if (at <= 0) {
            return false;
        }

        if (!ScopeId.TryParsePath(path[..at], out var scope) || scope.Kind != ScopeKind.Tenant) {
            return false;
        }

        var rest = path[(at + Suffix.Length)..];

        if (rest.Length == 0) {
            address = new(scope.TenantId, string.Empty);
            return true;
        }

        if (rest[0] != '/') {
            return false;
        }

        var number = Uri.UnescapeDataString(rest[1..]);
        if (number.Length is 0 or > MaxNumberLength || !number.All(static x => char.IsAsciiLetterOrDigit(x) || x == '-')) {
            return false;
        }

        address = new(scope.TenantId, number);
        return true;
    }

    /// <summary>The same address in another tenant — what the gateway rebuilds from the token.</summary>
    /// <param name="tenantId">The token's tenant.</param>
    public InvoiceAddress WithTenant(Guid tenantId) => this with { TenantId = tenantId };

    /// <inheritdoc />
    public override string ToString() => Path;
}
