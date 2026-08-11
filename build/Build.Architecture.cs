// Architecture — docs/plan/23 § The architecture gates, the enforcement half of
// docs/plan/00 § Non-negotiables.

using Serilog;

partial class Build
{
    /// <summary>
    ///     The ten gates from docs/plan/23 § The architecture gates, in the order the doc lists
    ///     them. Named here so the target's log output is the checklist, and so adding a gate is a
    ///     visible diff against the doc rather than a silent omission.
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
