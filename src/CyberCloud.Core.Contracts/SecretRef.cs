namespace CyberCloud.Core.Contracts;

/// <summary>
///     A handle to a secret. The value lives in OpenBao and never in grain state.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/00 § Non-negotiables, the "Secrets never reach grain state" row:
///         <i>"secrets are <c>SecretRef</c> handles resolved at the data plane"</i>. ⚠ Every member
///         here is an address, and there is deliberately no member that could hold a value — the same
///         absence argument <see cref="Error" /> makes about stack traces. A nullable <c>Value</c>
///         "for convenience" would be populated by the first caller who found resolving inconvenient,
///         and from then on every backup of the durable tier would contain it.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Here rather than in <c>CyberCloud.ResourceManager.Contracts</c>, where it started, and
///             the alias is deliberately unchanged.
///         </b> The rule is written platform-wide —
///         docs/plan/00 § Non-negotiables and docs/plan/05 § What is not in a grain — so a module that
///         wanted to obey it had to either take a dependency on the resource manager's contracts (and
///         through them on the Kubernetes and tenancy contracts, for one three-field record) or
///         declare its own. <c>CyberCloud.Identity.Contracts</c> did the second, and said so; a
///         platform-wide rule with two incompatible spellings of its own vocabulary is the rule
///         eroding.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The <c>[Alias]</c> stays <c>CyberCloud.ResourceManager.SecretRef</c>, and moving it
///             would be the data-loss bug this move exists to avoid.
///         </b> docs/plan/04 § Failure and
///         upgrade makes the alias — not the CLR name, not the namespace — what a silo of version N
///         looks up when a silo of version N+1 sends it a payload. Renaming a type is free under that
///         rule and re-spelling its alias is not, so the alias records where the concept was first
///         published and nothing about this move touches the wire.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.SecretRef")]
public sealed record SecretRef {
    /// <summary>The vault path, for example <c>tenants/{tenantId}/postgres/main</c>.</summary>
    [Id(0)]
    public string Path { get; init; } = string.Empty;

    /// <summary>Which field at that path, for example <c>adminPassword</c>.</summary>
    [Id(1)]
    public string Field { get; init; } = string.Empty;

    /// <summary>
    ///     The version to read, or empty for the current one. Pinning a version is what makes a
    ///     reconcile pass reproducible across a rotation.
    /// </summary>
    [Id(2)]
    public string Version { get; init; } = string.Empty;

    /// <summary>Whether this handle names something.</summary>
    /// <remarks>
    ///     ⚠ A handle with no path or no field is not "an empty secret" — it is an address that
    ///     resolves to nothing, and a caller that passed one meant to pass a real one. Callers refuse
    ///     it rather than resolving it and getting an empty string back.
    /// </remarks>
    public bool IsEmpty => Path.Length == 0 || Field.Length == 0;

    /// <summary>
    ///     Whether <see cref="Path" /> addresses exactly what it spells —
    ///     <see cref="IsCanonicalPath" /> of it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Two fixes of one defect, merged into one rule (2026-09-24).</b> The #34 review found
    ///     <c>tenants/{a}/../{b}/db</c> passing a tenant-prefix check in the mailbox parser and added
    ///     this property and <see cref="IsConfinedTo" />; #30's second review found
    ///     <c>tenants/{a}/../../platform/…</c> reaching a key vault's root and added
    ///     <see cref="IsCanonicalPath" />, which also refuses <c>\</c>, <c>%</c> and control characters.
    ///     The stricter one is the definition, and every check reads it.
    /// </remarks>
    public bool IsCanonical => IsCanonicalPath(Path);

    /// <summary>
    /// <summary>
    ///     Whether a tenant-spelled vault path is inside <paramref name="prefix" />: canonical, under it,
    ///     and naming something below it rather than the prefix itself.
    /// </summary>
    /// <param name="path">The path as a resource body spelled it.</param>
    /// <param name="prefix">The tenant's own prefix, ending in <c>/</c> — for example <c>tenants/{tenantId}/</c>.</param>
    /// <returns><see langword="true" /> when resolving <paramref name="path" /> can only reach under <paramref name="prefix" />.</returns>
    /// <remarks>
    ///     ⚠ One rule for every type that lets a tenant name a vault path, so the traversal check
    ///     <see cref="IsCanonical" /> describes cannot be present in one parser and missing from the
    ///     next — the first two copies of the prefix check in this tree both lacked it.
    /// </remarks>
    public static bool IsConfinedTo(string path, string prefix) {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentException.ThrowIfNullOrEmpty(prefix);

        return path.Length > prefix.Length
            && path.StartsWith(prefix, StringComparison.Ordinal)
            && IsCanonicalPath(path);
    }

    /// <summary>
    ///     Reports whether a vault path names the one location its segments spell, so a check on its
    ///     text is a check on where it leads.
    /// </summary>
    /// <param name="path">A vault path, for example <c>tenants/{tenantId}/postgres/main</c>.</param>
    /// <returns>
    ///     <c>true</c> if the path is non-empty and none of its <c>/</c>-separated segments is empty,
    ///     <c>.</c> or <c>..</c>, and it carries no <c>\</c>, <c>%</c> or control character.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A prefix check on a path that isn't canonical is no check.</b>
    ///         <c>tenants/{a}/../../platform/CyberCloud.KeyVault/vaults/{a}/{id}</c> starts with tenant
    ///         <c>a</c>'s prefix. <c>Uri.EscapeDataString</c> leaves <c>..</c> alone, and
    ///         <see cref="System.Uri" /> collapses dot segments before the request leaves, so OpenBao
    ///         is asked for the vault root under <c>platform/</c> with the platform's broad token. So
    ///         every place that resolves or mints a path refuses one that fails this, and every place
    ///         that checks a tenant-supplied path against a prefix calls it first.
    ///     </para>
    ///     <para>
    ///         ⚠ <c>%</c> and <c>\</c> are refused as well, although neither reaches a dot segment
    ///         today: the resolver escapes <c>%</c> to <c>%25</c> and <c>\</c> to <c>%5C</c>. A path
    ///         the platform writes never needs either, and refusing them means a second decode
    ///         somewhere between here and OpenBao can't turn <c>%2e%2e</c> back into <c>..</c>.
    ///     </para>
    /// </remarks>
    public static bool IsCanonicalPath(string path) {
        if (string.IsNullOrEmpty(path)) {
            return false;
        }

        foreach (var segment in path.Split('/')) {
            if (segment is "" or "." or "..") {
                return false;
            }
        }

        foreach (var c in path) {
            if (c is '\\' or '%' || char.IsControl(c)) {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Version.Length == 0 ? $"{Path}#{Field}" : $"{Path}#{Field}@{Version}";
}
