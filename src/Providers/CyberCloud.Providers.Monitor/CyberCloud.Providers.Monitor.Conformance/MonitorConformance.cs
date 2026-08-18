using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
// ⚠ For ResourceId, which HarnessAddress needs. Every other provider's conformance project gets away
// without it because ProviderConformanceCase's own members carry the address; this one needs to build
// one, for the reason MonitorCase.HarnessAddress records.
using CyberCloud.Core.Resources;
using CyberCloud.Providers.Monitor.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.Conformance;

/// <summary>
///     <c>CyberCloud.Monitor/workspaces</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One case object and two class declarations, which is the twelfth time that number has
///         held.</b> What this one adds is a resource whose objects are <i>configuration for a data
///         plane the platform runs</i> rather than a description of a workload — three objects, two
///         core kinds and one custom, none of which becomes a pod.
///     </para>
///     <para>
///         ⚠⚠ <b><see cref="ProviderConformanceCase.ObjectMatchesDesired" /> IS THE ONLY MEMBER
///         THAT COST ANYTHING, AND IT COST A SECOND <c>Matches</c>.</b> The member's signature is
///         <c>(objectJson, desiredJson)</c> — the limit <c>StorageBuckets</c> records and
///         <c>AgentPools</c> demonstrated — and <b>everything this type renders is keyed on the
///         resource's own GUID</b>: the accountID in the <c>VMUser</c>'s path suffix, the database
///         name in the row, all three object names. This is the first family where that limit is not
///         an inconvenience but a structural gap, because identity is not one field of this type's
///         output, it <i>is</i> its output.
///     </para>
///     <para>
///         ⚠ <b>Measured rather than assumed.</b> The first version of this case closed over a fixed
///         address and ran <b>5 of 29 red</b> — the harness's own workspace has a different GUID, so
///         every accountID, database and object name compared against a workspace that does not
///         exist. Passing a made-up address makes the suite green and the comparison meaningless, so
///         the case calls <see cref="MonitorWorkspaces.MatchesShape" /> instead, which checks the
///         half that does not depend on the address and says so in its own name.
///     </para>
///     <para>
///         ⚠ <b>What that leaves uncovered is the worst bug this type can have</b> — a render that
///         put every workspace on one accountID, which is every tenant reading every other tenant's
///         metrics. <c>MonitorReconcilerTests.TwoWorkspacesInTwoTenantsGetTwoAccountIdsAndTwoDatabases</c>
///         is what catches it, and it is a hand-written test for exactly the reason
///         <c>AgentPools.ObjectNameOf</c>'s collision needed one.
///     </para>
///     <para>
///         ⚠ <b>No child type, and the reason is a platform seam rather than scope.</b> docs/plan/16
///         calls ingest keys a sub-resource and says <i>"rotatable with a grace period"</i>. A grace
///         period is two live credentials at once; <c>ISecretWriter</c> mints once and does not
///         replace, so a second key cannot be created without invalidating the first. Second
///         sighting of the blocker <c>CyberCloud.Storage/accounts</c> records against
///         <c>regenerateKeys</c>, and the first where it decides a whole type rather than one
///         action.
///     </para>
/// </remarks>
public sealed class MonitorCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Monitor/workspaces",
            CreateProvider = () => new MonitorProvider(),
            ReconcilerType = typeof(MonitorWorkspaceReconciler),
            CreateReconciler = clock => new MonitorWorkspaceReconciler(clock),
            Type = MonitorWorkspaces.Type,
            ApiVersion = MonitorWorkspaces.V2026,
            Body = cluster => MonitorWorkspaces.Body(cluster),
            // ⚠ IT LENGTHENS THE LOGS RETENTION AND RAISES THE ALLOWANCE, AND BOTH HALVES ARE
            // DELIBERATE. The update test asserts the change reached the cluster, so it has to move
            // something the reconciler applies — `standard` changes the row's retentionLogsDays from
            // 7 to 30 and both move the storage meter. ⚠ AND IT MUST NOT SHORTEN ANYTHING: this
            // reconciler REFUSES a retention shrink by design (see its remarks), so a ChangedBody
            // that lowered a tier would fail the shared update assertion for the one reason that is
            // not a defect. That is a constraint the suite cannot express and every future editor of
            // this line has to know, which is why it is written here rather than assumed.
            ChangedBody = cluster => MonitorWorkspaces.Body(cluster, logsTier: "standard", logsGbPerDay: 25),
            // Sets `overQuotaSampleRate` to zero, which the schema's Minimum refuses.
            // ⚠ Built from a valid body with one property overwritten rather than hand-written: a
            // hand-written invalid body drifts out of date the day the schema gains a property and
            // then tests "invalid for the wrong reason" while still going green.
            //
            // ⚠ AND THE PROPERTY IT BREAKS IS THE ONE CARRYING A PRODUCT PROMISE. docs/plan/16
            // forbids the silent drop; zero is a silent drop spelled as a rate; the Minimum of 1 is
            // the only part of that promise the API can keep on its own today. A conformance case
            // that broke `location` instead would have proved the write path refuses bodies, which
            // eleven other cases already prove.
            InvalidBody = cluster => WithZeroSampleRate(MonitorWorkspaces.Body(cluster)),
            InvalidBodyTarget = "/properties/quota/overQuotaSampleRate",
            ActionName = MonitorWorkspaces.ListKeysAction,
            // ⚠ IN APPLY ORDER, MATCHING THE RECONCILER. The suite does not require an order and the
            // reconciler's reason for having one is in its remarks; listing them differently here
            // would be a second, quieter opinion about which object comes first.
            Objects = (id, ns) => [
                MonitorWorkspaces.KeySecretRef(ns, id.Name),
                MonitorWorkspaces.VmUserRef(ns, id.Name),
                MonitorWorkspaces.RowRef(ns, id.Name)
            ],
            // ⚠ ONE FUNCTION OVER THREE KINDS, WHICH IS WHY MonitorWorkspaces.Matches DISPATCHES ON
            // `kind` AND RETURNS FALSE FOR ONE IT DOES NOT KNOW. A Matches that defaulted to true for
            // an unrecognised document would report a VMUser that was never applied as converged —
            // a workspace the ingest host believes in and vmauth refuses every write to.
            // ⚠ MonitorWorkspaces.MatchesShape, NOT MonitorWorkspaces.Matches, AND THE DIFFERENCE IS
            // THE FINDING RATHER THAN A SHORTCUT. See that method's remarks, and this class's.
            ObjectMatchesDesired = (objectJson, desiredJson) => {
                using var desired = JsonDocument.Parse(desiredJson);
                return MonitorWorkspaces.MatchesShape(objectJson, desired.RootElement);
            }
        };

    /// <summary>A valid body whose over-quota sample rate is zero.</summary>
    /// <param name="body">A valid body.</param>
    static string WithZeroSampleRate(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["quota"]!.AsObject()["overQuotaSampleRate"] = 0;
        return node.ToJsonString();
    }
}

/// <summary>The shared suite, run against the monitor-workspace provider.</summary>
/// <param name="cluster">The harness.</param>
public sealed class MonitorWorkspaceConformance(ProviderTestCluster<MonitorCase> cluster)
    : ProviderConformanceTests<MonitorCase>(cluster), IClassFixture<ProviderTestCluster<MonitorCase>>;

/// <summary>The container-backed half, skipped loudly, against the monitor-workspace provider.</summary>
public sealed class MonitorWorkspaceBackedConformance()
    : ClusterBackedConformanceTests(MonitorCase.ProviderCase);
