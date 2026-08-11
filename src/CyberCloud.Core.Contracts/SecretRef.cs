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
///         ⚠ <b>Here rather than in <c>CyberCloud.ResourceManager.Contracts</c>, where it started, and
///         the alias is deliberately unchanged.</b> The rule is written platform-wide —
///         docs/plan/00 § Non-negotiables and docs/plan/05 § What is not in a grain — so a module that
///         wanted to obey it had to either take a dependency on the resource manager's contracts (and
///         through them on the Kubernetes and tenancy contracts, for one three-field record) or
///         declare its own. <c>CyberCloud.Identity.Contracts</c> did the second, and said so; a
///         platform-wide rule with two incompatible spellings of its own vocabulary is the rule
///         eroding.
///     </para>
///     <para>
///         ⚠ <b>The <c>[Alias]</c> stays <c>CyberCloud.ResourceManager.SecretRef</c>, and moving it
///         would be the data-loss bug this move exists to avoid.</b> docs/plan/04 § Failure and
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

    /// <inheritdoc />
    public override string ToString() =>
        Version.Length == 0 ? $"{Path}#{Field}" : $"{Path}#{Field}@{Version}";
}
