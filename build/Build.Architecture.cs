// Architecture — docs/plan/23 § The architecture gates, the enforcement half of
// docs/plan/00 § Non-negotiables.

using Serilog;

partial class Build
{
    /// <summary>
    ///     The ten gates from docs/plan/23 § The architecture gates, in the order the doc lists
    ///     them. Named here so the target's log output is the checklist, and so adding a gate is a
    ///     visible diff against the doc rather than a silent omission.
    ///     <para>
    ///         ⚠ <b>Four of the ten are now enforced by the compiler instead, and this target must
    ///         not re-implement them.</b> <c>src/CyberCloud.Analyzers</c> ships CC1001–CC1007, which
    ///         every project under <c>src/</c> references as an analyzer asset:
    ///     </para>
    ///     <list type="bullet">
    ///         <item><b>Tenant keys</b> — the "no string literal containing '|' in a GetGrain argument" half is CC1004. The "every tenant-scoped grain interface is IGrainWithStringKey" half is still this target's.</item>
    ///         <item><b>Serializer discipline</b> — the "[Alias] on every [GenerateSerializer]" half is CC1003. The "[Id(n)] numbers never reused, checked against a committed manifest" half lives in <c>WireContractTests</c> and is still not here.</item>
    ///         <item><b>Secrets</b> — CC1005, in full.</item>
    ///         <item><b>No blocking</b> — CC1001 and CC1002. ⚠ Wider than the doc's wording: the doc says "in grain assemblies", the analyzers apply everywhere they are referenced, because a gateway that blocks is a stalled request even though it is not a stalled activation.</item>
    ///     </list>
    ///     <para>
    ///         A compile-time rule beats a build-target sweep for all four: it names the line, it
    ///         runs in the IDE, and it cannot be outrun by a file the sweep's glob missed. What it
    ///         cannot do is see across assemblies, which is why the other six stay here.
    ///     </para>
    /// </summary>
    static readonly (string Gate, string Checks)[] ArchitectureGates =
    [
        ("Assembly graph", "the six rules in docs/plan/03"),
        ("Storage tier", "every [PersistentState] against durable-grains.txt; a Durable binding outside the list needs [DurableStateRationale]"),
        ("Tenant keys", "no string literal containing '|' in a GetGrain argument; every tenant-scoped grain interface is IGrainWithStringKey"),
        ("Serializer discipline", "every [GenerateSerializer] type has a stable [Alias]; [Id(n)] numbers never reused, checked against a committed manifest"),
        ("Wire compatibility", "round-trip every wire type through the last three released contract assemblies"),
        ("Secrets", "no [Id] member named *Password/*Secret/*Token/*Key outside CyberCloud.Vault"),
        ("No blocking", ".Result, .Wait(), async void banned in grain assemblies"),
        ("Generated surfaces", "OpenAPI/CLI/SDK/forms regenerate byte-identically from the registry"),
        ("OpenAPI compatibility", "published api-versions diffed; a breaking change fails"),
        ("Labels", "every reconciler's rendered output carries the seven cybercloud.io/* labels, asserted against real output"),
    ];

    void CheckArchitecture()
    {
        NotImplementedYet(
            nameof(Architecture),
            $"run the {ArchitectureGates.Length} gates below, each failing the build with a message "
            + "naming the offending type and the rule",
            "docs/plan/23 § The architecture gates. Tracked as its own task (#12) — it is not part "
            + "of standing up the build system, and it cannot run against a repository with no "
            + "assemblies in it.");

        foreach (var (gate, checks) in ArchitectureGates)
            Log.Information("  gate (pending): {Gate} — {Checks}", gate, checks);
    }
}
