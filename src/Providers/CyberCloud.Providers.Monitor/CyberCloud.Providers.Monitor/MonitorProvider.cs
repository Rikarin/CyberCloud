using CyberCloud.Providers.Monitor.Query;

namespace CyberCloud.Providers.Monitor;

/// <summary>
///     Managed observability — a workspace over the platform's own telemetry stores, and the alert
///     rules evaluated against it.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/16 § <c>CyberCloud.Monitor/workspaces</c> · <b>M1 · 2.5 EM</b>:
///         <i>
///             "The tenant-facing resource. Owns retention, quota, ingest keys, and the datasource
///             wiring."
///         </i> ADR-016 chose the two engines; docs/plan/01 § The catalogue spells the row
///         <c>CyberCloud.Monitor/workspaces</c> and this is the path.
///     </para>
///     <para>
///         ⚠ <b>A WORKSPACE IS A TENANCY, NOT A DEPLOYMENT.</b> The five-part argument is on
///         <see cref="MonitorWorkspaces" /> and it is the first thing to read on this row. The short
///         form: docs/plan/16 routes ingest to an <c>accountID</c> and a database rather than to a
///         store; docs/plan/05 gives each store one row per region; and — decisively — docs/plan/16
///         puts <b>platform</b> telemetry under a workspace of this type, so a type that provisioned
///         the store would make the control plane's own reconcile depend on the store its reconcile
///         creates. <c>CyberCloud.Ingest.Host</c> is deliberately not an Orleans client to keep that
///         cycle broken, and this type is shaped to keep it broken from the other end.
///     </para>
///     <para>
///         ⚠ <b>THE TWELFTH FAMILY, AND THE FIRST WHOSE PRODUCT IS NOT A WORKLOAD AT ALL.</b>
///         <c>CyberCloud.ContainerService/managedClusters</c> established that a resource's product
///         need not be the objects it applies — there the objects are Cluster API's and the product
///         is a second Kubernetes cluster. Here the objects are the product's <i>whole</i>
///         mechanism on one half and its <i>announcement</i> on the other: the <c>VMUser</c> is
///         enforcement, and the <c>ConfigMap</c> is a row nothing reads yet. A provider whose objects
///         are configuration rather than infrastructure is a shape this catalogue had not had, and
///         it needed no fifth module edge and no seventh project.
///     </para>
///     <para>
///         ⚠
///         <b>
///             IT WAS THE FIRST TYPE IN THE TREE TO DECLARE <c>SupportsSoftDelete</c>, IT WITHDREW
///             THE DECLARATION, AND IT DECLARES IT AGAIN — ALL ON 2026-08-18.
///         </b> The case for the window
///         is recorded here because it survived the withdrawal unchanged, and the withdrawal is
///         recorded here because it is what closed the defect. Eleven families declined, each with
///         the same stated reason — the manager did not honour a window — and docs/plan/08 § Soft
///         delete endorsed the instinct and ended
///         <i>
///             "the declaration is the last step, not the
///             first"
///         </i>. What is left is the provider's own question:
///         <i>
///             does the data this type
///             carries deserve a recovery window, and how long
///         </i>. On this type the answer is the least
///         ambiguous in the catalogue. A workspace is the tenant's <b>only</b> copy of their logs — a
///         database has a backup, an object store has versioning, and telemetry has neither, because
///         the source of truth was a process that has since exited. docs/plan/16's closing sentence
///         is
///         <i>
///             "a monitoring product that quietly loses data is worse than no monitoring product,
///             because it is trusted"
///         </i>, and a delete with no window is the loudest possible version of
///         that. Seven days, which is docs/plan/06 § Tags, locks' number for a type carrying data,
///         with purge behind its own permission and a purge-protection flag on the body.
///     </para>
///     <para>
///         ⚠
///         <b>
///             AND THE RECOVERY WINDOW IS CHEAP HERE FOR THE SAME REASON THE TYPE IS NOT A
///             DEPLOYMENT.
///         </b> A soft-deleted workspace keeps its partitions and costs disk that was
///         already reserved and no compute at all. On the deployment-shaped reading, the same window
///         would mean keeping a cluster running for a week per deleted workspace, and somebody would
///         have quietly made it a soft delete that deletes.
///     </para>
///     <para>
///         ⚠⚠
///         <b>
///             THE WITHDRAWAL, KEPT IN FULL, BECAUSE IT IS THE MEASUREMENT THAT FIXED THE
///             PLATFORM.
///         </b> Two drafts of this paragraph were wrong before the third was checked. The
///         first said a soft-deleted workspace is one whose <c>VMUser</c> is gone,
///         <i>
///             "so nothing can
///             write to it"
///         </i>; the second kept the declaration and filed the gap as owed. Both
///         understated it. The facts, each read in the shipping source rather than inferred:
///     </para>
///     <list type="bullet">
///         <item>
///             <c>OperationGrain.DriveAsync</c> returned early for a soft delete and ran
///             <b>
///                 no pass
///                 at all
///             </b> — so <c>IResourceReconciler.DeleteAsync</c> was never called and every
///             object this provider applied was left exactly as it was.
///         </item>
///         <item>
///             <c>ParkAsync</c> said the rest in as many words:
///             <i>
///                 "Its quota stays committed until
///                 it is purged."
///             </i> The storage this workspace's retention and allowances draw was
///             still reserved against the subscription.
///         </item>
///         <item>
///             ⚠ <b>And the object left standing was the one that is NOT inert.</b> The
///             <c>ConfigMap</c> is read by nothing, because <c>CyberCloud.Ingest.Host</c> does not
///             exist — for it, "maintained" and "deleted" genuinely are the same state today. The
///             <c>VMUser</c> is the opposite: vmauth resolves it the moment it is applied, and it is
///             the one thing on this row that enforces anything at all.
///         </item>
///     </list>
///     <para>
///         Together those made a soft-deleted workspace an
///         <b>
///             authenticated, billed, open write path
///             into a store the tenant believed was gone
///         </b>: a collector nobody reconfigured kept
///         writing, the data kept landing in a tenancy whose address answered <c>404</c>, the
///         retention kept accruing against quota, and the tenant could see none of it — the only way
///         to stop it was a purge, which sits behind a permission they may not hold. ⚠ On a database
///         or an object store a recovery window merely holds disk; here it held an open ingest
///         endpoint, which is a difference docs/plan/08 did not anticipate because nothing before
///         this had declared a window.
///         <b>
///             A delete that does not delete is worse than no recovery
///             window
///         </b>, so the declaration was withdrawn until the platform could withdraw the write
///         path on park — the same conclusion <c>CyberCloud.ContainerRegistry/registries</c> reached
///         from its own measurement, for a reason that is worse here rather than merely similar.
///     </para>
///     <para>
///         ⚠
///         <b>
///             What did NOT reproduce, recorded because a finding that fails to replicate is worth
///             as much as one that does — and this one decided where the fix belonged.
///         </b> That row
///         measured a soft-deleted resource <i>reconciling its whole data plane back</i>. On this type
///         that path was not reachable, and it was checked rather than assumed: driving the completed
///         delete operation again returned nothing, and disabling <c>OperationGrain.DriveAsync</c>'s
///         soft-delete branch made the pass run with <c>tearingDown</c> <b>true</b> —
///         <c>OperationSpec.Kind</c> is <c>Delete</c>, so it <i>destroyed</i> the objects rather than
///         re-applying them. ⚠ <b>There was no re-apply anywhere.</b> The other row's evidence was a
///         conformance assertion reporting an END STATE, and an end state cannot tell "never torn
///         down" from "torn down and re-applied" — which mattered, because the two are different bugs
///         in different code and only one of them existed.
///     </para>
///     <para>
///         ⚠ <b>WHAT CLOSED IT, AND WHY THE DECLARATION IS BACK.</b> A soft delete now runs the
///         teardown exactly as a hard delete does, so the <c>VMUser</c> comes down with everything
///         else and the write path closes on convergence rather than at a purge nobody may be able to
///         authorise. What the window holds is what a teardown does not remove: the name, the
///         workspace's stored body, the committed quota, and — for the stores this row is a tenancy
///         <i>in</i> — the tenant's data, which no teardown of a <c>VMUser</c> or a <c>ConfigMap</c>
///         could have removed in the first place. A restore re-applies the same three objects through
///         <c>OperationKind.Restore</c> and the tenancy is back with its retention and its accountID
///         unchanged. <c>MonitorWorkspaceConformance.ASoftDeletedWorkspacesWritePathIsClosedAndNoStrayDriveReopensIt</c>
///         asserts all three halves; <c>conformance.yaml § owed</c>,
///         <c>soft-delete-was-withdrawn-and-is-declared-again</c>.
///     </para>
///     <para>
///         ⚠ <b>What is deliberately NOT declared, each with its reason.</b> No
///         <c>workspaces/ingestKeys</c> child type, although docs/plan/16 calls ingest keys a
///         sub-resource: that row also says <i>"rotatable with a grace period"</i>, and a grace
///         period is two live credentials at once, which <c>ISecretWriter</c>'s mint-once rule
///         cannot hold — the same blocker <c>CyberCloud.Storage/accounts</c> records against
///         <c>regenerateKeys</c>, now on its second sighting and on a type where it decides a whole
///         child rather than one action. No <c>dataSources</c> body property, for the reason
///         on <see cref="MonitorWorkspaces.ListKeysResponse" /> — it is an output.
///     </para>
///     <para>
///         ⚠
///         <b>
///             AND <c>workspaces/collectors</c> IS DECLARED (#32, the second noun), WHICH MAKES THIS
///             THE FIRST FAMILY WITH A CLUSTER-BACKED PARENT, A CLUSTERLESS CHILD AND A CHILD THAT
///             RUNS A POD.
///         </b> This paragraph used to say
///         <i>
///             "No <c>collectors</c>: M2 in docs/plan/16 and the half
///             of issue #32 this branch did not take."
///         </i> A collector is a Deployment of upstream's
///         collector image under the workspace, exporting into the workspace's stores exactly as the
///         workspace's <c>listKeys</c> addresses them — and it learns the workspace's coordinates
///         from the workspace's own row through the kubelet rather than from this pass, which is the
///         argument on <see cref="MonitorCollectors" />. The managed Grafana that reads the same
///         workspace is <see cref="DashboardProvider" />, the second provider in this assembly, under
///         docs/plan/01's own namespace for it.
///     </para>
///     <para>
///         ⚠
///         <b>
///             AND <c>workspaces/alertRules</c> IS DECLARED (#32), WHICH MAKES THIS THE FIRST
///             FAMILY WITH A CLUSTER-BACKED PARENT AND A CLUSTERLESS CHILD.
///         </b> The paragraph above used to end "No <c>collectors</c> and no <c>alertRules</c>".
///         A rule is a condition over the workspace's own stores, a severity and an action group
///         naming a <c>CyberCloud.Communication/services</c> resource; it applies nothing to any
///         cluster and converges onto <see cref="IAlertEvaluatorGrain" /> — one per workspace, on a
///         reminder, the first grain in a provider's implementation assembly. Its remarks and
///         <see cref="MonitorAlertRules" />' carry the argument; the type's own declaration below
///         carries what it declares and does not.
///     </para>
///     <para>
///         ⚠
///         <b>
///             AND <c>workspaces/components</c> IS DECLARED (#32, the fourth noun), THE FIRST TYPE
///             WHOSE ACTIONS READ A TELEMETRY STORE.
///         </b> A component is an application inside the workspace: one <c>ConfigMap</c> holding the
///         connection string, and five views — requests, dependencies, exceptions, the application
///         map and a transaction — read on the request path from the workspace's ClickHouse database,
///         whose name the handler derives from the workspace's GUID in the platform's index
///         (<c>ActionContext.Parent</c>). <see cref="MonitorComponents" /> carries the argument.
///     </para>
/// </remarks>
public sealed class MonitorProvider : IResourceProvider {
    /// <inheritdoc />
    public string ProviderNamespace => MonitorWorkspaces.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType(MonitorWorkspaces.TypePath)
            .ApiVersion(MonitorWorkspaces.V2026, MonitorWorkspaces.Schema2026)
            .Reconciler<MonitorWorkspaceReconciler>()
            // ⚠ ONE DERIVED METER AND THE COUNT, AND THE DERIVATION IS A FIFTH SHAPE.
            //
            //   CyberCloud.DBforPostgreSQL/servers  an amount is a quantity STRING, not a number
            //   CyberCloud.Messaging/natsClusters   a PRODUCT of a replica count and one figure
            //   CyberCloud.Storage/accounts         a SUM over HETEROGENEOUS components
            //   CyberCloud.Analytics/clickhouseCl…  a PRODUCT and a SUM at once
            //   here                                a SUM over three signals of a product whose
            //                                       FIRST FACTOR IS NOT IN THE BODY AT ALL
            //
            // ⚠ THAT LAST DIFFERENCE IS THE ONE THAT MATTERS. Every earlier derivation multiplies
            // numbers the tenant typed. This one multiplies a GB/day allowance the tenant typed by a
            // day count that comes from MonitorWorkspaces.RetentionDays — a platform table selected
            // by a tier NAME the tenant typed. So the meter reads `/properties/retention/logs` and
            // gets back the string "standard", and the number 30 is the platform's. A derivation
            // that read the pointer and expected a number would derive nothing and reserve nothing.
            //
            // ⚠ AND THIS IS WHERE docs/plan/16 § Cost and retention honesty IS SATISFIED RATHER THAN
            // AGREED WITH. That section requires retention to be "a paid property"; the only way a
            // property is paid is if it moves the amount the platform reserves, and this is the line
            // where it does. MonitorQuotaTests.MovingOnlyTheRetentionTierMovesTheStorageAmount is
            // what fails if somebody ever "simplifies" the derivation to GB/day alone.
            //
            // ⚠ EACH DERIVATION IS A PURE FUNCTION OF THE BODY AND MUST STAY ONE. The delete path
            // re-derives committed amounts from the resource's stored body through the same step the
            // create reserved with — ResourceManagerService.CommittedBy — so a derivation that read
            // a clock or a configuration would make a delete return a different number than the
            // create committed, and quota would drift upward on every create/delete cycle.
            .Meter(QuotaMeter.StorageGb, StorageDrawn)
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            // ⚠ SYNCHRONOUS, WITH A HANDLER, WHICH IS THE ONLY SHAPE THAT ANSWERS THIS QUESTION.
            // `longRunning: false` with no handler is refused by name at declaration time, and
            // `longRunning: true` with a handler is refused by ProviderBuilder.Action — a
            // long-running action goes through the operation grain and re-runs the RECONCILER, which
            // for a listKeys would answer 202 and hand back nothing. This is the second handler in
            // the tree after CyberCloud.Storage/accounts'.
            .Action(
                MonitorWorkspaces.ListKeysAction,
                ActionKind.Post,
                MonitorWorkspaces.ListKeysPermission,
                true,
                response: MonitorWorkspaces.ListKeysResponse,
                handler: typeof(MonitorWorkspaceListKeysHandler)
            )
            // ── The explorers' three reads — #41, docs/plan/16 § Querying a workspace ─────────────
            //
            // ⚠ ACTIONS AND NOT A ROUTE OF THEIR OWN, AND `read` AND NOT A PERMISSION OF THEIR OWN.
            // MonitorQueries' remarks carry both arguments: the action path already resolves the
            // address with the token's tenant, 404s what does not exist, checks through the one seam
            // and hands the handler the GUID the accountID and the database are derived from; and a
            // query reads the workspace, which is what Reader is for.
            //
            // ⚠ TWO OF THE THREE DECLARE NO RESPONSE. A series list and a row list are arrays of
            // objects, which SchemaKind.Array refuses; the dispatcher leaves an undeclared response
            // unchecked and the generated clients type it `unknown`. conformance.yaml § owed,
            // query-responses-are-undeclared.
            .Action(
                MonitorQueries.QueryMetricsAction,
                ActionKind.Post,
                MonitorQueries.Permission,
                request: MonitorQueries.QueryMetricsRequest,
                handler: typeof(MonitorWorkspaceQueryMetricsHandler)
            )
            .Action(
                MonitorQueries.ListMetricLabelsAction,
                ActionKind.Post,
                MonitorQueries.Permission,
                request: MonitorQueries.ListMetricLabelsRequest,
                response: MonitorQueries.ListMetricLabelsResponse,
                handler: typeof(MonitorWorkspaceListMetricLabelsHandler)
            )
            .Action(
                MonitorQueries.SearchLogsAction,
                ActionKind.Post,
                MonitorQueries.Permission,
                request: MonitorQueries.SearchLogsRequest,
                handler: typeof(MonitorWorkspaceSearchLogsHandler)
            )
            // ⚠ `workspace`, AND `monitor` IS THE ONE WORD THIS NAMESPACE COULD NOT HAVE.
            // CliEmitter derives the CLI GROUP key from the provider namespace's last segment,
            // lower-cased, so this namespace is already the group `monitor` — and a short name equal
            // to its OWN group's key gives `cyc monitor monitor` two meanings. CliTokens carries the
            // rule and CliTokenTests carries the measurements.
            //
            // ⚠ THE LIST THAT USED TO SAY SO IS GONE, AND IT ASKED THE WRONG QUESTION. This
            // paragraph held twelve group keys and fifteen short names as literals, and the same list
            // in the network suite was stale on two consecutive passes. Measured against
            // System.CommandLine 2.0.10, the token dictionary is per PARENT command, so a short name
            // equal to ANOTHER group's key cannot collide at all — eleven of those twelve
            // comparisons could never have failed. ProviderRegistry.Build now derives the question
            // from what is registered and refuses the silo naming both ends;
            // MonitorDeclarationTests.NoShortNameHereGivesACycTokenTwoMeanings asks it for this
            // provider.
            .Display(
                "Monitor workspace",
                "Monitor workspaces",
                "workspace",
                "A tenancy in the platform's metrics, logs and traces stores, with its own "
                + "retention, quota, ingest key and read-only datasource endpoints."
            )
            .Chart(MonitorWorkspaces.ChartName)
            // ⚠ THE RECOVERY WINDOW, WITHDRAWN ON 2026-08-18 AND RESTORED THE SAME DAY, AND BOTH
            // HALVES ARE WORTH KEEPING. It was withdrawn because a soft delete ran no reconcile pass
            // at all: every applied object was left exactly as it was, and on this type the object
            // left standing is the VMUser — the one thing on this row that enforces anything, because
            // vmauth resolves it the moment it is applied. So a soft-deleted workspace was an
            // authenticated, BILLED, open write path into a store the tenant believed was gone.
            //
            // ⚠ WHAT CHANGED IS THE PLATFORM AND NOT THE ARGUMENT. OperationGrain.DriveAsync now runs
            // the teardown for a soft delete, so the VMUser comes down with everything else and the
            // write path closes; what the window holds is the name, the workspace's stored body, the
            // committed quota and — for the stores this row is a tenancy IN — the data itself, which
            // no teardown of a VMUser or a ConfigMap could remove. A restore re-applies the same three
            // objects and the tenancy is back with its retention and its accountID unchanged.
            .SupportsSoftDelete(SoftDeleteDays, purgeProtectionPointer: MonitorWorkspaces.PurgeProtectionPointer)
            .SupportsTags()
            .RequiresCluster()
            // ── workspaces/alertRules — #32 ─────────────────────────────────────────────────────
            //
            // ⚠ NO RequiresCluster AND NO Chart ON A CHILD WHOSE PARENT HAS BOTH, AND BOTH ABSENCES
            // ARE THE DECLARATION. A rule applies nothing anywhere: it converges a spec onto its
            // workspace's evaluator grain and the evaluator does the rest on a reminder. Declaring
            // RequiresCluster would make the reconcile driver refuse a pass with no connection for a
            // reconciler that never reads one, and the conformance suite would then read this type
            // through the fake cluster and find nothing — ProviderConformanceCase.Objects' remarks
            // say what that run would have proved. Clusterless, with an IConvergedModule, is the
            // honest registration; test/CyberCloud.Conformance learnt the shape from the sending
            // module and this is the first time it meets it under a cluster-backed parent.
            //
            // ⚠ ONE METER, THE COUNT. A rule draws no vCPU, memory, storage or address. What it
            // costs the platform is a query per interval on a shared store, which is bounded by the
            // three limits MonitorAlertRules records rather than by quota — a query is not a thing
            // a subscription HOLDS, which is the argument CyberCloud.Communication.Contracts' .csproj
            // makes about a message.
            //
            // ⚠ NO SupportsSoftDelete. A rule's history is what a window would protect, and the
            // history is on the workspace's evaluator, which the workspace's own seven-day window
            // already holds — a deleted rule under a live workspace is one PUT to recreate and its
            // instances are an event log, not the tenant's only copy of anything.
            .ResourceType(MonitorAlertRules.TypePath)
            .ApiVersion(MonitorWorkspaces.V2026, MonitorAlertRules.Schema2026)
            .Reconciler<MonitorAlertRuleReconciler>()
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            // ⚠ SYNCHRONOUS, WITH A HANDLER, AND IT REACHES A GRAIN FROM THE REQUEST PATH — the
            // shape the sending module's four actions established. A long-running listInstances
            // would answer 202 and re-run the reconciler, which for a read is nothing.
            .Action(
                MonitorAlertRules.ListInstancesAction,
                ActionKind.Post,
                "read",
                response: MonitorAlertRules.ListInstancesResponse,
                handler: typeof(MonitorAlertRuleListInstancesHandler)
            )
            .Display(
                "Alert rule",
                "Alert rules",
                "alert",
                "A condition over the workspace's metrics or logs, evaluated on a schedule; when it "
                + "holds for long enough the action group is told through a Communication service, and "
                + "again when it stops."
            )
            .SupportsTags()
            // ── workspaces/collectors — #32, the second noun ────────────────────────────────────
            //
            // ⚠ RequiresCluster AND Chart ON A CHILD WHOSE SIBLING HAS NEITHER, AND BOTH PRESENCES
            // ARE THE DECLARATION. A collector is three objects in a cluster — a ConfigMap, a
            // Deployment and a Service — so it is the shape AgentPools established: a child with its
            // own clusterId, converged onto the fake cluster in the Docker-free suite and onto a real
            // kubelet in the cluster-backed one, where the pod has to START and accept an export.
            //
            // ⚠ TWO METERS FROM A PRESET, AND THE COUNT. A collector is a pod; it draws vCPU and
            // memory the way loadBalancers does, multiplied by its replica count, because three
            // replicas of a small preset reserve three pods' worth and not one.
            //
            // ⚠ NO SupportsSoftDelete. A collector holds nothing: its state is its configuration,
            // which is a function of the body, and the telemetry it carried is in the workspace,
            // which has the window.
            .ResourceType(MonitorCollectors.TypePath)
            .ApiVersion(MonitorWorkspaces.V2026, MonitorCollectors.Schema2026)
            .Reconciler<MonitorCollectorReconciler>()
            .Meter(QuotaMeter.Vcpu, CollectorVcpuDrawn)
            .Meter(QuotaMeter.MemoryGb, CollectorMemoryDrawn)
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            // ⚠ SYNCHRONOUS, WITH A HANDLER, AND NOT SECRET: an endpoint inside the cluster is an
            // address, and the collector's ingress is unauthenticated by design and by record —
            // conformance.yaml § owed, collector-ingress-is-unauthenticated.
            .Action(
                MonitorCollectors.ListEndpointsAction,
                ActionKind.Post,
                MonitorCollectors.ListEndpointsPermission,
                response: MonitorCollectors.ListEndpointsResponse,
                handler: typeof(MonitorCollectorListEndpointsHandler)
            )
            .Display(
                "OpenTelemetry collector",
                "OpenTelemetry collectors",
                "collector",
                "A managed OpenTelemetry collector in your cluster that your workloads send OTLP "
                + "to, carrying metrics, logs and traces into this workspace."
            )
            .Chart(MonitorCollectors.ChartName)
            .SupportsTags()
            .RequiresCluster()
            // ── workspaces/components — #32, the fourth noun ────────────────────────────────────
            //
            // ⚠ RequiresCluster AND Chart FOR ONE ConfigMap, BECAUSE THE CONNECTION STRING IS A THING A
            // POD MOUNTS. The views need no cluster at all — they read the workspace's ClickHouse
            // database from the request path — but a connection string a workload has to be handed by
            // hand is the step every tenant gets wrong, so it is published where envFrom can name it.
            //
            // ⚠ ONE METER, THE COUNT. A component runs nothing and stores nothing: its telemetry is
            // the workspace's, reserved by the workspace's storage meter. What a view costs is a query
            // on a shared store, bounded by the look-back cap and the store's per-query budget, which
            // is the alert rule's argument about a query not being a thing a subscription HOLDS.
            //
            // ⚠ NO SupportsSoftDelete. The data a window would protect is the workspace's, and the
            // workspace has the window; a deleted component is one PUT to bring back and its views
            // return exactly what they did, because they never held anything.
            .ResourceType(MonitorComponents.TypePath)
            .ApiVersion(MonitorWorkspaces.V2026, MonitorComponents.Schema2026)
            .Reconciler<MonitorComponentReconciler>()
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Action(
                MonitorComponents.ListConnectionStringAction,
                ActionKind.Post,
                MonitorComponents.Permission,
                response: MonitorComponents.ListConnectionStringResponse,
                handler: typeof(MonitorComponentConnectionStringHandler)
            )
            // ⚠ FIVE ACTIONS, ONE HANDLER, AND EACH IS SYNCHRONOUS. A view answers the caller or
            // refuses; a long-running one would answer 202 and re-run the reconciler, which for a read
            // is nothing (the listInstances argument). They run in the gateway's process, which is
            // where the telemetry store is reached from — MonitorTelemetryServiceCollectionExtensions.
            .Action(
                MonitorComponents.RequestsAction,
                ActionKind.Post,
                MonitorComponents.Permission,
                request: MonitorComponents.ViewRequest,
                response: MonitorComponents.RequestsResponse,
                handler: typeof(MonitorComponentViewHandler)
            )
            .Action(
                MonitorComponents.DependenciesAction,
                ActionKind.Post,
                MonitorComponents.Permission,
                request: MonitorComponents.ViewRequest,
                response: MonitorComponents.DependenciesResponse,
                handler: typeof(MonitorComponentViewHandler)
            )
            .Action(
                MonitorComponents.ExceptionsAction,
                ActionKind.Post,
                MonitorComponents.Permission,
                request: MonitorComponents.ViewRequest,
                response: MonitorComponents.ExceptionsResponse,
                handler: typeof(MonitorComponentViewHandler)
            )
            .Action(
                MonitorComponents.ApplicationMapAction,
                ActionKind.Post,
                MonitorComponents.Permission,
                request: MonitorComponents.ViewRequest,
                response: MonitorComponents.ApplicationMapResponse,
                handler: typeof(MonitorComponentViewHandler)
            )
            .Action(
                MonitorComponents.TransactionAction,
                ActionKind.Post,
                MonitorComponents.Permission,
                request: MonitorComponents.TransactionRequest,
                response: MonitorComponents.TransactionResponse,
                handler: typeof(MonitorComponentViewHandler)
            )
            .Display(
                "Application component",
                "Application components",
                "component",
                "An application inside the workspace: the connection string its SDKs send through a "
                + "collector, and its requests, dependencies, exceptions, map and transactions read back "
                + "from the workspace's traces and logs."
            )
            .Chart(MonitorComponents.ChartName)
            .SupportsTags()
            .RequiresCluster();
    }

    /// <summary>vCPU: the sizing preset's cpu, in cores, times the replica count.</summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         A product, like <c>natsClusters</c>', and the second factor is the one an obvious
    ///         derivation forgets.
    ///     </b> Three replicas of <c>c1.small</c> are three pods requesting
    ///     <c>250m</c> each; a reservation of <c>250m</c> would let a subscription at its vCPU limit
    ///     schedule two more pods than it paid for.
    /// </remarks>
    static MeterDerivation CollectorVcpuDrawn { get; } =
        MeterDerivation.Of(
            "sizing.preset's cpu, in cores, times replicas",
            ["/properties/sizing/preset", "/properties/replicas"],
            static body => KubeQuantity.TryParse(MonitorCollectors.Resources(body).Cpu, out var cores)
                ? Result<decimal>.Success(cores * MonitorCollectors.Replicas(body))
                : Result<decimal>.Failure(
                    ErrorCode.InternalError,
                    "the sizing preset behind '/properties/sizing/preset' carries a cpu quantity that "
                    + "does not parse, so no reservation can be computed"
                )
        );

    /// <summary>Memory: the sizing preset's memory, in GiB, times the replica count.</summary>
    static MeterDerivation CollectorMemoryDrawn { get; } =
        MeterDerivation.Of(
            "sizing.preset's memory, in GiB, times replicas",
            ["/properties/sizing/preset", "/properties/replicas"],
            static body => KubeQuantity.TryGibibytes(MonitorCollectors.Resources(body).Memory, out var gibibytes)
                ? Result<decimal>.Success(gibibytes * MonitorCollectors.Replicas(body))
                : Result<decimal>.Failure(
                    ErrorCode.InternalError,
                    "the sizing preset behind '/properties/sizing/preset' carries a memory quantity "
                    + "that does not parse, so no reservation can be computed"
                )
        );

    /// <summary>How long a deleted workspace stays recoverable.</summary>
    /// <remarks>
    ///     docs/plan/06 § Tags, locks gives 7 for a type carrying data —
    ///     <i>
    ///         "a dropped production
    ///         database is not a support ticket you want to have to say no to"
    ///     </i>. ⚠ It is a
    ///     <b>type-level</b> number and therefore immutable by construction, which is what
    ///     docs/plan/08 § Soft delete asks for:
    ///     <i>
    ///         "a window a caller can shorten under their own
    ///         resource is not a recovery window"
    ///     </i>. There is no per-resource retention property for a
    ///     caller to shorten, and the delete path stamps the window from this constant.
    /// </remarks>
    public const int SoftDeleteDays = 7;

    // ── What a workspace draws ────────────────────────────────────────────────────────────────
    //
    // ⚠ NO `vcpu` AND NO `memoryGb`, AND THE REASON IS THE SAME ONE CyberCloud.Network/virtualNetworks
    // GIVES RATHER THAN THE QuotaGrain ONE THREE OTHER TYPES GIVE. A workspace provisions no pods:
    // the stores it is a tenancy in were running before it existed and keep running after it is
    // gone. There is nothing attributable to derive, so the axis is ABSENT rather than conditional,
    // and QuotaGrain.TryReserveAsync's "a reservation must be positive; 0 is not" refusal — which is
    // what blocks a CONDITIONAL meter on natsClusters, kafkaClusters and managedClusters — is not
    // what is happening here. ⚠ Second sighting of that distinction on the compute axis and the
    // first on a type that is not a network object, which is what makes it a property of "does this
    // resource run anything" rather than of networking.
    //
    // ⚠ AND `storageGb` IS PRESENT WHERE virtualNetworks HAS ONLY `Resources`, WHICH IS THE
    // DIFFERENCE BETWEEN THE TWO. A Vpc is a logical router with no disk. A workspace's whole cost
    // is disk, and the tenant sets both factors of it.

    /// <summary>
    ///     Storage: the gibibytes at rest this workspace's retention and daily allowances entitle it
    ///     to, summed over the three signals.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A CEILING RATHER THAN A SIZE, WHICH IS A SHAPE ONLY ONE OTHER TYPE HAS.</b>
    ///         <c>CyberCloud.ContainerService/managedClusters/agentPools</c> reserves <c>maxCount</c>
    ///         rather than <c>count</c> because an autoscaler moves the real number. Here the real
    ///         number is what the tenant actually sends, which the platform cannot know at create
    ///         time and which docs/plan/22's usage pipeline samples separately. So this reserves what
    ///         the workspace is <i>allowed</i> to accumulate. ⚠ A reservation is not a bill, and the
    ///         two disagreeing is not a defect in either.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             It cannot be zero, and the schema is what guarantees that rather than this
    ///             method.
    ///         </b> Every <c>*GbPerDay</c> has <c>Minimum = 1</c> and every retention tier
    ///         maps to a positive day count, so the sum is at least three — which keeps this meter
    ///         clear of <c>QuotaGrain.TryReserveAsync</c>'s non-positive refusal on every legal
    ///         body. A tier added to <see cref="MonitorWorkspaces.Tiers" /> without a row in
    ///         <see cref="MonitorWorkspaces.RetentionDays" /> would derive zero days for that signal,
    ///         which is why the refusal below exists and why
    ///         <c>MonitorRetentionTests.EveryTierOfEverySignalHasADayCount</c> is a test rather than
    ///         a comment.
    ///     </para>
    /// </remarks>
    static MeterDerivation StorageDrawn { get; } =
        MeterDerivation.Of(
            "the sum over metrics, logs and traces of (retention tier's days) × (that signal's "
            + "quota in GiB/day), in GiB",
            [
                "/properties/retention/metrics",
                "/properties/retention/logs",
                "/properties/retention/traces",
                "/properties/quota/metricsGbPerDay",
                "/properties/quota/logsGbPerDay",
                "/properties/quota/tracesGbPerDay"
            ],
            static body => MonitorWorkspaces.StorageCeilingGb(body) is > 0 and var ceiling
                ? Result<decimal>.Success(ceiling)
                : Result<decimal>.Failure(
                    ErrorCode.InternalError,
                    "The storage a monitor workspace draws came out at zero or less, which no legal "
                    + "body can produce: every quota property has a minimum of 1 and every retention "
                    + "tier has a positive day count. The likeliest cause is a tier in "
                    + "MonitorWorkspaces.Tiers with no row in MonitorWorkspaces.RetentionDays. The "
                    + "write is refused rather than reserved at zero, because a resource that "
                    + "provisions against no quota is one nobody is charged for — docs/plan/06 "
                    + "§ Quota."
                )
        );
}
