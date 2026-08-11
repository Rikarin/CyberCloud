namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     The eleven steps of docs/plan/08 § The write path, end to end, numbered as that document
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
///         <c>WritePathStepOrderTests</c> asserts that the recorded sequence is strictly increasing
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

    /// <summary>Write durable desired state and register the reconcile reminder.</summary>
    SubmitDesired = 8,

    /// <summary>Start the long-running operation.</summary>
    StartOperation = 9,

    /// <summary>Emit <c>resource-changed</c>.</summary>
    EmitChanged = 10,

    /// <summary><c>202 Accepted</c>, with the operation URL and a <c>Retry-After</c>.</summary>
    Accepted = 11
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

/// <summary>
///     A lock level, inherited down the hierarchy — docs/plan/06 § Tags, locks, and the small stuff
///     that is not small.
/// </summary>
[Alias("CyberCloud.ResourceManager.LockLevel")]
public enum LockLevel {
    /// <summary>No lock.</summary>
    None = 0,

    /// <summary>Writes and deletes are both refused.</summary>
    ReadOnly = 1,

    /// <summary>Writes are allowed; deletes are refused.</summary>
    CanNotDelete = 2
}

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

    /// <summary>A JSON array. Element shape is not modelled; that is a later api-version's problem.</summary>
    Array = 6
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
