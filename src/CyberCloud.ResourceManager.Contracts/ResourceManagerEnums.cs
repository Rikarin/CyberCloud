namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     The twelve steps of docs/plan/08 § The write path, end to end, numbered as that document
///     numbers them.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This enum exists so that step order is a testable property rather than a review
///         convention.</b> docs/plan/08 § The write path, end to end:
///         <i>
///             "Steps 3-7 are the entire reason this is one component rather than a shared library
///             each provider calls. A provider that could skip step 3 is a provider that eventually
///             will."
///         </i>
///         The write path records each step it enters into a <see cref="WriteTrace" />, and
///         <c>WritePathTests.EveryTraceIsAStrictlyIncreasingPrefixOfTheCanonicalOrder</c> asserts
///         that the recorded sequence is strictly increasing
///         and is a prefix of the canonical order. Moving any step earlier or later fails that test
///         without anyone having to notice the move.
///     </para>
///     <para>
///         <b>The values are the document's numbers</b>, so a trace reads as the document does. That
///         is also why <see cref="None" /> is zero rather than the first step — a default value must
///         not name a real step, or an unrecorded trace would read as "step 1 ran".
///     </para>
/// </remarks>
[Alias("CyberCloud.ResourceManager.WriteStep")]
public enum WriteStep {
    /// <summary>Never assigned. Not a step.</summary>
    None = 0,

    /// <summary>Parse the path to a <see cref="ResourceId" /> and resolve provider, type and api-version.</summary>
    ResolveRegistration = 1,

    /// <summary>Validate the body against the type's schema for that api-version.</summary>
    ValidateBody = 2,

    /// <summary>
    ///     ReBAC <c>Check</c>. ⚠ <b>Before quota, before the index, before the provider is ever
    ///     called.</b> docs/plan/07 § The enforcement seam.
    /// </summary>
    AuthorizationCheck = 3,

    /// <summary><c>CanNotDelete</c> / <c>ReadOnly</c> locks inherited from the group, subscription and management group.</summary>
    Locks = 4,

    /// <summary>Policy evaluation — deny, modify, audit. M3; see <see cref="PolicyEffect" />.</summary>
    Policy = 5,

    /// <summary>Quota reservation. <c>429</c> naming which meter.</summary>
    Quota = 6,

    /// <summary>The two-phase create's name claim. <c>409</c> if taken.</summary>
    IndexClaim = 7,

    /// <summary>
    ///     Write the ReBAC <c>parent</c> edge — <c>resource:{id}#parent@resourceGroup:{sub}-{rg}</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Before <see cref="SubmitDesired" />, and the ordering is the entire decision.</b> The
    ///     resource's GUID exists from <see cref="Quota" /> and its name is claimed at
    ///     <see cref="IndexClaim" />, so the edge can be written before any durable resource state
    ///     does. Written <i>after</i> <see cref="SubmitDesired" /> there would be a window in which the
    ///     resource exists and is invisible to the person who just created it, and a silo lost inside
    ///     that window would leave it invisible permanently. Written here, a failure is a clean refusal:
    ///     nothing durable has been written, the quota lease is released and the index claim expires.
    /// </remarks>
    LinkParent = 8,

    /// <summary>Write durable desired state and register the reconcile reminder.</summary>
    SubmitDesired = 9,

    /// <summary>Start the long-running operation.</summary>
    StartOperation = 10,

    /// <summary>Emit <c>resource-changed</c>.</summary>
    EmitChanged = 11,

    /// <summary><c>202 Accepted</c>, with the operation URL and a <c>Retry-After</c>.</summary>
    Accepted = 12
}

/// <summary>
///     Which of <see cref="ReconcileOutcome" />'s three cases this is.
/// </summary>
/// <remarks>
///     ⚠ <b>Three, and no more.</b> docs/plan/08 § The reconcile loop:
///     <i>
///         "<c>ReconcileOutcome</c> is one of <c>Converged</c>, <c>InProgress(reason, retryAfter)</c>,
///         <c>Failed(error, retryable)</c>. Nothing else."
///     </i>
///     There is no <c>Unknown</c> member, which is the one deviation from this repository's
///     "zero means never assigned" convention — see the remarks on <see cref="ReconcileOutcome" />
///     for why a default outcome is unrepresentable instead.
/// </remarks>
[Alias("CyberCloud.ResourceManager.ReconcileOutcomeKind")]
public enum ReconcileOutcomeKind {
    /// <summary>The reconciler <b>read back</b> the desired shape. Not "an apply returned 200".</summary>
    Converged = 1,

    /// <summary>Work is under way. Carries a reason and how long to wait.</summary>
    InProgress = 2,

    /// <summary>It did not work. Carries the error and whether retrying could help.</summary>
    Failed = 3
}

/// <summary>
///     The Azure long-running-operation statuses of docs/plan/08 § Long-running operations, exactly.
/// </summary>
/// <remarks>
///     ⚠ <b>Named <c>OperationState</c> and not <c>OperationStatus</c> deliberately</b> —
///     docs/plan/08 § Long-running operations uses the one word for two things, the enum
///     ("Statuses are Azure's — <c>NotStarted</c>, <c>Running</c>, …") and the payload of
///     <c>GetAsync</c> ("<c>Task&lt;Result&lt;OperationStatus&gt;&gt; GetAsync()</c>"). They cannot
///     both be <c>OperationStatus</c>, so the record keeps the document's name and the enum takes a
///     different one.
/// </remarks>
[Alias("CyberCloud.ResourceManager.OperationState")]
public enum OperationState {
    /// <summary>Never assigned.</summary>
    Unknown = 0,

    /// <summary>Accepted, nothing driven yet.</summary>
    NotStarted = 1,

    /// <summary>Being driven.</summary>
    Running = 2,

    /// <summary>Terminal success.</summary>
    Succeeded = 3,

    /// <summary>Terminal failure.</summary>
    Failed = 4,

    /// <summary>
    ///     Terminal cancellation. ⚠ Reached only after the delete path has run for everything already
    ///     applied — docs/plan/08 § Long-running operations, "cancellation <i>completes</i> rather
    ///     than abandoning".
    /// </summary>
    Canceled = 5
}

/// <summary>What an operation is doing to its resource.</summary>
[Alias("CyberCloud.ResourceManager.OperationKind")]
public enum OperationKind {
    /// <summary>Never assigned.</summary>
    Unknown = 0,

    /// <summary>A <c>PUT</c> on a name that was free.</summary>
    Create = 1,

    /// <summary>A <c>PUT</c> or <c>PATCH</c> on a name that was taken.</summary>
    Update = 2,

    /// <summary>A <c>DELETE</c>.</summary>
    Delete = 3,

    /// <summary>A <c>POST</c> action on an existing resource. ⚠ Never creates.</summary>
    Action = 4
}

/// <summary>
///     The write verbs, and the grammar docs/plan/08 § The write path, end to end copies from Azure.
/// </summary>
/// <remarks>
///     ⚠ <b>The verb is where retry-safety lives.</b> docs/plan/08:
///     <i>
///         "This is Azure's grammar and it is copied because it makes retry-safety a property of the
///         verb rather than of each provider's care."
///     </i>
///     A repeated identical <see cref="Put" /> is a no-op; a repeated <see cref="Patch" /> is not
///     guaranteed to be; <see cref="Post" /> never creates.
/// </remarks>
[Alias("CyberCloud.ResourceManager.WriteVerb")]
public enum WriteVerb {
    /// <summary>Never assigned.</summary>
    Unknown = 0,

    /// <summary>A full replacement, and idempotent.</summary>
    Put = 1,

    /// <summary>RFC 7386 JSON Merge Patch, and <b>not</b> idempotent in general.</summary>
    Patch = 2,

    /// <summary>An action on an existing resource. ⚠ Never creates.</summary>
    Post = 3,

    /// <summary>A delete.</summary>
    Delete = 4
}

// ⚠ LockLevel USED TO LIVE HERE AND NOW LIVES IN CyberCloud.Tenancy.Contracts.
//
// The move is not tidying. docs/plan/06 § Tags, locks makes a lock a property of the HIERARCHY —
// "inherited down" it — and the hierarchy is docs/plan/06's, not this document's: the scopes that
// carry a lock are the resource group and the subscription, whose grains live in
// CyberCloud.Tenancy.Contracts. That assembly cannot reference this one (this one references it), so
// as long as the enum lived here a resource group could not name its own lock and
// ResourceScopeLockResolver had nothing above the resource to read. That was defect 3.
//
// The [Alias] is unchanged — "CyberCloud.ResourceManager.LockLevel" — so the move is invisible on the
// wire (docs/plan/04 § Failure and upgrade). Every consumer already imports CyberCloud.Tenancy.Contracts
// globally, which is why nothing else in the repository had to change.

/// <summary>How an action is invoked. docs/plan/08 § The provider registry.</summary>
[Alias("CyberCloud.ResourceManager.ActionKind")]
public enum ActionKind {
    /// <summary>Never assigned.</summary>
    Unknown = 0,

    /// <summary><c>POST /…/{name}/{action}</c> — <c>restart</c>, <c>rotateKeys</c>, <c>listKeys</c>.</summary>
    Post = 1
}

/// <summary>
///     What a schema property holds. The subset the registry's declarative schema needs, and no more.
/// </summary>
/// <remarks>
///     ⚠ <b>Deliberately not "any JSON Schema".</b> docs/plan/08 § The provider registry requires the
///     registry to be <i>the same object</i> that generates the four surfaces and validates the body.
///     A schema expressed as opaque JSON Schema text validates but does not generate — an emitter
///     would have to re-parse and re-interpret it, which is exactly the second interpretation that
///     makes drift possible again. So the schema is a typed model this assembly owns, and JSON Schema
///     is one of the things ADR-012 will <i>emit</i> from it rather than the format it is stored in.
///     <para>
///         ⚠ <b>The members are not spelled the way JSON spells them, and the reason is CA1720.</b>
///         <c>String</c>, <c>Integer</c> and <c>Object</c> are all identifiers CA1720 refuses — the
///         repository builds with warnings as errors and <c>AnalysisMode=Recommended</c> — so they are
///         <see cref="Text" />, <see cref="WholeNumber" /> and <see cref="Nested" />. The mapping onto
///         JSON's own names is on each member and is what the generated OpenAPI will emit; it is
///         stated here rather than left for a reader to infer from <c>Nested</c>.
///     </para>
/// </remarks>
[Alias("CyberCloud.ResourceManager.SchemaKind")]
public enum SchemaKind {
    /// <summary>Never assigned. Not a kind.</summary>
    Unknown = 0,

    /// <summary>A JSON string.</summary>
    Text = 1,

    /// <summary>A JSON number.</summary>
    Number = 2,

    /// <summary>A JSON integer — a number with no fractional part.</summary>
    WholeNumber = 3,

    /// <summary>A JSON boolean.</summary>
    Boolean = 4,

    /// <summary>A JSON object. Its members are separate properties with deeper pointers.</summary>
    Nested = 5,

    /// <summary>
    ///     A JSON array. ⚠ Its element shape is <see cref="Registry.SchemaProperty.ElementKind" /> — a
    ///     <i>scalar</i> kind, not a nested tree.
    /// </summary>
    /// <remarks>
    ///     An array of objects is deliberately not expressible: an element schema would need its own
    ///     pointer space (<c>/properties/rules/0/port</c> addresses one element, not the shape), and
    ///     the flat pointer list is what makes <see cref="Registry.ResourceSchema.Validate" /> an index rather
    ///     than a tree walk. Declaring <see cref="Registry.SchemaProperty.ElementKind" /> as <see cref="Nested" /> is refused
    ///     rather than half-modelled — docs/plan/20 § The shape that makes 100 resource types
    ///     affordable maps "array of objects" onto <c>@xui/data-table</c>, and that is the api-version
    ///     that will need the tree.
    /// </remarks>
    Array = 6
}

/// <summary>
///     The semantic refinement of a <see cref="SchemaKind.Text" />, and the one keyword every OpenAPI
///     consumer already knows how to act on.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A format is <i>checked</i>, not merely printed.</b> JSON Schema makes <c>format</c>
///         an annotation that validators may ignore; <see cref="Registry.ResourceSchema.Validate" /> does not
///         ignore it. A registry fact that the write path did not enforce would be exactly the
///         "documentation rather than a contract" failure docs/plan/08 § The provider registry exists
///         to prevent — the generated document and the runtime have to agree in both directions.
///     </para>
///     <para>
///         <b>Four are JSON Schema's own and two are this platform's.</b> The platform pair is spelled
///         with a <c>cybercloud-</c> prefix in the emitted document so that a generic OpenAPI tool
///         treats them as the unknown formats they are rather than guessing.
///     </para>
/// </remarks>
[Alias("CyberCloud.ResourceManager.SchemaFormat")]
public enum SchemaFormat {
    /// <summary>Never assigned. The value is an unrefined string.</summary>
    None = 0,

    /// <summary>
    ///     A GUID in hyphenated <c>D</c> form. ⚠ Braced, parenthesised and bare-hex forms are refused,
    ///     for the reason <see cref="GuidFormat" /> gives: one resource, one spelling.
    /// </summary>
    Uuid = 1,

    /// <summary>An RFC 3339 timestamp. ⚠ Offset required — a local time has no meaning on the wire.</summary>
    DateTime = 2,

    /// <summary>An absolute URI. A relative one is refused: the body is not resolved against a base.</summary>
    Uri = 3,

    /// <summary>An e-mail address, checked for shape rather than for deliverability.</summary>
    Email = 4,

    /// <summary>
    ///     A Cyber Cloud region — <c>eu-central</c>. Emitted as <c>cybercloud-region</c> and mapped to
    ///     docs/plan/20's region picker.
    /// </summary>
    Region = 5,

    /// <summary>
    ///     A fully qualified <see cref="ResourceId" /> path. Emitted as
    ///     <c>cybercloud-resource-id</c>; checked with <see cref="ResourceId.TryParsePath" />, which is
    ///     the same parser the gateway uses, so a body that names a resource names one that could
    ///     exist.
    /// </summary>
    ResourceId = 6
}

/// <summary>
///     Which control docs/plan/20 § The shape that makes 100 resource types affordable renders a
///     property with — ADR-012's promised <c>x-cybercloud-*</c> hint.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>ADR-012 promised this and <see cref="Registry.SchemaProperty" /> had no slot for it, which is
///         the plan and the code contradicting each other.</b> docs/plan/02 § ADR-012's portal row
///         reads <i>"Angular reactive forms + xUI controls from the schema, with <c>x-cybercloud-*</c>
///         hints for widgets (a <c>storageclass</c> picker, a region picker)"</i>. With nowhere to
///         declare one, the portal emitter would have had to infer a widget from a property's
///         <i>name</i> — and a renderer that keys on "the property is called region" is a second
///         interpretation of the schema, which is the drift ADR-012 exists to remove.
///     </para>
///     <para>
///         <b>The members are docs/plan/20's list, exactly.</b> That document names
///         <c>region, cluster, storageclass, subnet, sku, secret-ref, cron, cidr, duration</c> plus
///         <c>x-cozy-preset</c>. A tenth widget is a change to that document first and to this enum
///         second, so a provider cannot invent a control the portal has no code for.
///     </para>
///     <para>
///         ⚠ <b>A hint is a hint. It is not validation and it never narrows the body.</b>
///         <see cref="SchemaFormat" /> and <see cref="Registry.SchemaProperty.Pattern" /> are where a
///         constraint goes. A widget that implied a constraint would let a provider tighten the API by
///         changing a rendering hint, and the compatibility diff deliberately treats every
///         <c>x-</c> extension as prose.
///     </para>
/// </remarks>
[Alias("CyberCloud.ResourceManager.WidgetHint")]
public enum WidgetHint {
    /// <summary>Never assigned. The renderer picks from <c>type</c>, <c>enum</c> and <c>format</c>.</summary>
    None = 0,

    /// <summary>Region picker with capacity and latency hints.</summary>
    Region = 1,

    /// <summary>Cluster picker, filtered by subscription and capability.</summary>
    Cluster = 2,

    /// <summary>Storage-class picker.</summary>
    StorageClass = 3,

    /// <summary>Subnet picker.</summary>
    Subnet = 4,

    /// <summary>Sku picker.</summary>
    Sku = 5,

    /// <summary>A Vault <c>SecretRef</c> picker. ⚠ Never a plain value — docs/plan/20.</summary>
    SecretRef = 6,

    /// <summary>Cron-expression editor.</summary>
    Cron = 7,

    /// <summary>CIDR editor.</summary>
    Cidr = 8,

    /// <summary>Duration editor.</summary>
    Duration = 9,

    /// <summary>The <c>t1.micro</c>/<c>c1.large</c> family picker of docs/plan/12.</summary>
    CozyPreset = 10,

    /// <summary>A key/value bag — <c>@xui/tag-input</c>.</summary>
    TagInput = 11
}

/// <summary>
///     What policy evaluation decided — step 5 of docs/plan/08 § The write path, end to end.
/// </summary>
/// <remarks>
///     ⚠ <b><see cref="NotSupported" /> is the honest default and is not a failure.</b> Policy is M3.
///     The seam is here so the write path has the step in the right place from the start; the default
///     <c>IPolicyEvaluator</c> returns <see cref="NotSupported" />, the write path treats that as
///     "carry on", and the day a real evaluator is registered nothing about the ordering has to move.
///     A default that returned <see cref="Allow" /> would be indistinguishable from a policy engine
///     that evaluated and permitted, which is the difference an audit log has to be able to state.
/// </remarks>
[Alias("CyberCloud.ResourceManager.PolicyEffect")]
public enum PolicyEffect {
    /// <summary>No evaluator is registered. The write proceeds and the audit says so.</summary>
    NotSupported = 0,

    /// <summary>Evaluated and permitted.</summary>
    Allow = 1,

    /// <summary>Evaluated and refused. Carries an <see cref="ErrorCode.PolicyViolation" />.</summary>
    Deny = 2,

    /// <summary>Evaluated, permitted, and the body was rewritten.</summary>
    Modify = 3,

    /// <summary>Evaluated, permitted, and recorded.</summary>
    Audit = 4
}

/// <summary>What happened to a resource, for the <c>resource-changed</c> stream.</summary>
/// <remarks>
///     docs/plan/08 § The resource-graph projection consumes these. ⚠ The projector itself is out of
///     scope here — this assembly defines the <i>emission</i> and <c>IResourceChangedSink</c> is the
///     seam a projector plugs into.
/// </remarks>
[Alias("CyberCloud.ResourceManager.ResourceChangeKind")]
public enum ResourceChangeKind {
    /// <summary>Never assigned.</summary>
    Unknown = 0,

    /// <summary>Desired state was written for a name that was free.</summary>
    Created = 1,

    /// <summary>Desired state was rewritten.</summary>
    Updated = 2,

    /// <summary>A delete was accepted. ⚠ The resource is still visible, in <c>Deleting</c>.</summary>
    Deleting = 3,

    /// <summary>Teardown finished and the grain state is gone.</summary>
    Deleted = 4,

    /// <summary>The provisioning state moved without the desired body changing.</summary>
    StateChanged = 5
}

/// <summary>
///     The two things per-cluster drift detection finds that nothing else would — docs/plan/08
///     § The reconcile loop.
/// </summary>
[Alias("CyberCloud.ResourceManager.DriftKind")]
public enum DriftKind {
    /// <summary>Never assigned.</summary>
    Unknown = 0,

    /// <summary>
    ///     A labelled cluster object whose resource grain is gone — <b>deleted and billed for</b>.
    /// </summary>
    Orphan = 1,

    /// <summary>
    ///     A resource whose objects vanished — <b>somebody <c>kubectl delete</c>d production</b>.
    /// </summary>
    Stray = 2,

    /// <summary>Both sides exist and the reconcile hash differs.</summary>
    Diverged = 3
}
