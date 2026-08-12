using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace CyberCloud.Core;

/// <summary>
///     The closed, source-controlled registry of Cyber Cloud error codes.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/08 § Errors:
///         <i>
///             "<c>code</c> is a stable, documented, greppable identifier. It
///             is part of the API contract; changing one is a breaking change. There is a checked-in
///             registry and a build gate on additions."
///         </i>
///     </para>
///     <para>
///         This type <b>is</b> that registry, and it is closed by construction: the constructor is
///         private, so the only <see cref="ErrorCode" /> instances that can ever exist are the
///         <c>static readonly</c> fields declared below. A caller cannot invent a code, cannot pass
///         a free string where a code is expected, and cannot typo one — the compiler stops all
///         three. Every code appears exactly once in source, which is what makes
///         <c>grep -rn "QuotaExceeded"</c> answer the question "where can this come from".
///     </para>
///     <para>
///         <b>The gate on additions.</b> <see cref="All" /> is a hand-written list, and
///         <c>ErrorCodeRegistryTests</c> asserts (a) that it matches the declared fields exactly and
///         (b) that it matches a golden set of code strings. Adding a code therefore requires
///         editing three places in one commit, all of them reviewable. Wiring the same assertion
///         into <c>build/Build.Architecture.cs</c> is the remaining half of docs/plan/08 § Errors and
///         belongs with that file, not this one.
///     </para>
///     <para>
///         Codes are compared by reference (there is one instance per code) and by
///         <see cref="Value" /> ordinally, which are the same relation.
///     </para>
/// </remarks>
public sealed class ErrorCode : IEquatable<ErrorCode> {
    /// <summary>
    ///     The seven statuses this registry maps onto, named so a declaration reads as a sentence.
    /// </summary>
    /// <remarks>
    ///     ⚠ Plain <c>int</c> constants rather than <see cref="System.Net.HttpStatusCode" />: the
    ///     value reaches a generated OpenAPI document as a three-digit response key and a JSON number,
    ///     and an enum that has to be cast at every use is an enum that will eventually be cast
    ///     wrongly. Naming them here keeps <c>= new("QuotaExceeded", 429)</c> from being a magic
    ///     number twenty-four times.
    /// </remarks>
    static class Status {
        public const int BadRequest = 400;
        public const int Forbidden = 403;
        public const int NotFound = 404;
        public const int Conflict = 409;
        public const int PreconditionFailed = 412;
        public const int TooManyRequests = 429;
        public const int InternalServerError = 500;
    }

    // ── The registry. Every code below cites the document that requires it. ────────────────────
    // Ordering is alphabetical so that a diff on this block is readable.

    /// <summary>
    ///     The caller is not permitted to perform this operation. docs/plan/08 § The write path,
    ///     end to end.
    /// </summary>
    /// <remarks>
    ///     ⚠ Never returned to a caller who may not read the resource — docs/plan/00 § Non-negotiables requires
    ///     <c>404</c>, not <c>403</c>, because existence is not disclosed. Use
    ///     <see cref="ResourceNotFound" /> there.
    /// </remarks>
    public static readonly ErrorCode AuthorizationFailed = new("AuthorizationFailed", Status.Forbidden);

    /// <summary>A concurrent change lost. docs/plan/06 § Two-phase create (<c>409</c> on a taken name).</summary>
    public static readonly ErrorCode Conflict = new("Conflict", Status.Conflict);

    /// <summary>An unexpected fault. Carries no exception detail — docs/plan/08 § Errors.</summary>
    public static readonly ErrorCode InternalError = new("InternalError", Status.InternalServerError);

    /// <summary>The <c>api-version</c> is unknown or retired. docs/plan/08 § The provider registry.</summary>
    public static readonly ErrorCode InvalidApiVersion = new("InvalidApiVersion", Status.BadRequest);

    /// <summary>A grain key within a tenant is malformed. ADR-002, docs/plan/02 § ADR-002.</summary>
    /// <remarks>
    ///     Also carried by <c>GrainKeys.NormalizeEmail</c>, whose failure means "no email index key
    ///     can be minted for this string". A user-facing sign-up validator should say so in its own
    ///     words rather than surfacing this code — it names the mechanism, not the field.
    /// </remarks>
    public static readonly ErrorCode InvalidGrainKey = new("InvalidGrainKey", Status.BadRequest);

    /// <summary>The request body failed the type's JSON Schema. docs/plan/08 § The write path, end to end.</summary>
    public static readonly ErrorCode InvalidRequestBody = new("InvalidRequestBody", Status.BadRequest);

    /// <summary>A resource id path is malformed. docs/plan/06 § Identifiers.</summary>
    public static readonly ErrorCode InvalidResourceId = new("InvalidResourceId", Status.BadRequest);

    /// <summary>
    ///     A resource group or resource name breaks the naming rules. docs/plan/06 § Identifiers.
    /// </summary>
    /// <remarks>
    ///     The message for this code is the mitigation the document asks for: it names the offending
    ///     value and character, states the rule, and says why the rule is strict.
    /// </remarks>
    public static readonly ErrorCode InvalidResourceName = new("InvalidResourceName", Status.BadRequest);

    /// <summary>A provider namespace or resource type is malformed. docs/plan/08 § The provider registry.</summary>
    public static readonly ErrorCode InvalidResourceType = new("InvalidResourceType", Status.BadRequest);

    /// <summary>The operation was cancelled by a caller. docs/plan/08 § Long-running operations.</summary>
    public static readonly ErrorCode OperationCanceled = new("OperationCanceled", Status.Conflict);

    /// <summary>Another operation holds the resource. docs/plan/03 § Providers (<c>409</c>).</summary>
    public static readonly ErrorCode OperationInProgress = new("OperationInProgress", Status.Conflict);

    /// <summary>An operation exceeded its budget. docs/plan/08 § The reconcile loop (60 minutes).</summary>
    public static readonly ErrorCode OperationTimeout = new("OperationTimeout", Status.InternalServerError);

    /// <summary>Policy evaluation denied the request. docs/plan/08 § The write path, end to end.</summary>
    public static readonly ErrorCode PolicyViolation = new("PolicyViolation", Status.Forbidden);

    /// <summary>
    ///     An <c>If-Match</c> etag did not match. docs/plan/06 § Tags, locks, and the small stuff
    ///     that is not small.
    /// </summary>
    public static readonly ErrorCode PreconditionFailed = new("PreconditionFailed", Status.PreconditionFailed);

    /// <summary>Reconciliation failed terminally. docs/plan/08 § The reconcile loop (<c>Failed</c>).</summary>
    public static readonly ErrorCode ProvisioningFailed = new("ProvisioningFailed", Status.InternalServerError);

    /// <summary>The name is already claimed in this scope. docs/plan/06 § Two-phase create.</summary>
    public static readonly ErrorCode ResourceAlreadyExists = new("ResourceAlreadyExists", Status.Conflict);

    /// <summary>The resource group does not exist. docs/plan/06 § The hierarchy.</summary>
    public static readonly ErrorCode ResourceGroupNotFound = new("ResourceGroupNotFound", Status.NotFound);

    /// <summary>
    ///     A delete was refused because the resource still has child resources. docs/plan/08
    ///     § Deleting a parent resource that has children.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A code of its own rather than <see cref="ScopeLocked" />, and the document's sentence
    ///     is the reason rather than an exception to it.</b> docs/plan/08 says the refusal <i>"reuses
    ///     the shape <c>ScopeLocked</c> already has — 'you cannot delete this yet, here is what is
    ///     holding it' — rather than inventing a second one"</i>. That shape is the <b>message</b>: a
    ///     409 that names the blocker so the caller can go and remove it. The <i>code</i> is what a
    ///     client library branches on, and <see cref="ScopeLocked" />'s own summary is "a lock forbids
    ///     the write or the delete" — so reusing it would tell a caller with no lock anywhere in their
    ///     hierarchy to go and find one, and would make "remove the lock" and "delete the children"
    ///     indistinguishable to every generated SDK. Same status, same message shape, different
    ///     recovery, therefore a different code.
    /// </remarks>
    public static readonly ErrorCode ResourceHasChildren = new("ResourceHasChildren", Status.Conflict);

    /// <summary>
    ///     The resource does not exist <i>or</i> the caller may not see it. docs/plan/00 § Non-negotiables.
    /// </summary>
    public static readonly ErrorCode ResourceNotFound = new("ResourceNotFound", Status.NotFound);

    /// <summary>
    ///     A lock forbids the write or the delete. docs/plan/06 § Tags, locks, and the small stuff
    ///     that is not small.
    /// </summary>
    public static readonly ErrorCode ScopeLocked = new("ScopeLocked", Status.Conflict);

    /// <summary>
    ///     A check or a tuple named an object type, relation or permission the authorization schema
    ///     does not define. docs/plan/07 § The model.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A distinguishable failure — never a denial, never an allow.</b> docs/plan/07 § The
    ///     model:
    ///     <i>
    ///         "A typo'd permission name in a text DSL is a silent allow-nothing or, worse in
    ///         the wrong evaluator, a silent allow-everything."
    ///     </i>
    ///     Returning a denial would be the
    ///     first and returning <c>Success(allowed: true)</c> the second, so the engine returns this
    ///     instead: an outcome that can be alerted on rather than served. The enforcement seam
    ///     (docs/plan/07 § The enforcement seam) still renders it to the caller as <c>404</c>; the
    ///     difference is that it also appears on a dashboard.
    /// </remarks>
    public static readonly ErrorCode SchemaInvalid = new("SchemaInvalid", Status.NotFound);

    /// <summary>The subscription does not exist. docs/plan/06 § The hierarchy.</summary>
    public static readonly ErrorCode SubscriptionNotFound = new("SubscriptionNotFound", Status.NotFound);

    /// <summary>The tenant does not exist. docs/plan/06 § The hierarchy.</summary>
    public static readonly ErrorCode TenantNotFound = new("TenantNotFound", Status.NotFound);

    /// <summary>
    ///     The tenant is <c>Suspended</c> and control-plane writes are rejected. docs/plan/06 § Tenant lifecycle.
    /// </summary>
    public static readonly ErrorCode TenantSuspended = new("TenantSuspended", Status.Forbidden);

    /// <summary>
    ///     A quota meter would be exceeded. docs/plan/08 § The write path, end to end and the worked example at
    ///     docs/plan/08 § Errors.
    /// </summary>
    public static readonly ErrorCode QuotaExceeded = new("QuotaExceeded", Status.TooManyRequests);

    // ── The closed set, and the lookup built from it. ──────────────────────────────────────────

    /// <summary>Every code in the registry, in declaration order.</summary>
    /// <remarks>
    ///     ⚠ Hand-written rather than reflected on purpose: reflection over static fields is
    ///     trim-hostile and, more importantly, invisible. A list you have to edit is a list a
    ///     reviewer sees. <c>ErrorCodeRegistryTests.EveryDeclaredCodeIsInAll</c> is the safety net
    ///     for the "forgot to add it here" mistake.
    /// </remarks>
    public static ImmutableArray<ErrorCode> All { get; } = [
        AuthorizationFailed,
        Conflict,
        InternalError,
        InvalidApiVersion,
        InvalidGrainKey,
        InvalidRequestBody,
        InvalidResourceId,
        InvalidResourceName,
        InvalidResourceType,
        OperationCanceled,
        OperationInProgress,
        OperationTimeout,
        PolicyViolation,
        PreconditionFailed,
        ProvisioningFailed,
        ResourceAlreadyExists,
        ResourceGroupNotFound,
        ResourceHasChildren,
        ResourceNotFound,
        SchemaInvalid,
        ScopeLocked,
        SubscriptionNotFound,
        TenantNotFound,
        TenantSuspended,
        QuotaExceeded
    ];

    /// <summary>The value-to-code lookup behind <see cref="TryFromValue" />.</summary>
    /// <remarks>
    ///     ⚠ <b>This declaration must stay below <see cref="All" />.</b> Static field initializers
    ///     run in declaration order, so moving it above <see cref="All" /> makes it read a default
    ///     <see cref="ImmutableArray{T}" /> and the whole type fails to initialize:
    ///     <c>TypeInitializationException</c> wrapping "This operation cannot be performed on a
    ///     default instance of ImmutableArray&lt;T&gt;". Because <see cref="ErrorCode" /> is
    ///     touched by <see cref="Result" />, that surfaces as every call in the process throwing,
    ///     nowhere near this file.
    ///     <para>
    ///         This is not hypothetical — a reformatting pass moved it above and broke 40 tenancy
    ///         tests at once. The compiler does not warn, and no analyzer catches it.
    ///     </para>
    /// </remarks>
    static readonly FrozenDictionary<string, ErrorCode> ByValue =
        All.ToFrozenDictionary(x => x.Value, StringComparer.Ordinal);

    /// <summary>The wire form — the exact string that appears in <c>error.code</c>.</summary>
    public string Value { get; }

    /// <summary>
    ///     The HTTP status a gateway renders this code as.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This lives on the code because it was living in the OpenAPI emitter, derived from
    ///         the plan's prose.</b> That emitter's own remarks reported it as a gap: docs/plan/07
    ///         § The enforcement seam fixes 404-versus-403 and docs/plan/10 § API versioning fixes the
    ///         400, and nothing in the tree mapped the other twenty-odd codes onto a status — so the
    ///         generator wrote one list and the gateway would have had to agree with it by hand.
    ///         Two hand-maintained copies of a mapping is the drift ADR-012 exists to remove, and the
    ///         registry of codes is the only place both can read.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="ResourceNotFound" /> and <see cref="SchemaInvalid" /> are both
    ///         <c>404</c>, and that is the point rather than an oversight.</b>
    ///         docs/plan/00 § Non-negotiables forbids disclosing existence, so "you may not read this"
    ///         and "the authorization schema is broken" reach the caller identically. The difference
    ///         is that one of them also appears on a dashboard — see <see cref="SchemaInvalid" />'s
    ///         own remarks.
    ///     </para>
    ///     <para>
    ///         <see cref="PreconditionFailed" /> is <c>412</c> rather than <c>409</c>: an
    ///         <c>If-Match</c> that did not match is exactly what HTTP defines <c>412</c> for, and a
    ///         client library's conditional-request retry keys on it.
    ///     </para>
    /// </remarks>
    public int HttpStatus { get; }

    ErrorCode(string value, int httpStatus) {
        Value = value;
        HttpStatus = httpStatus;
    }

    /// <summary>
    ///     The statuses codes are rendered as, ascending and each once.
    /// </summary>
    /// <remarks>
    ///     ⚠ Derived from <see cref="All" /> rather than listed, so a code added with a status nothing
    ///     else uses adds that response to every generated operation without anyone editing a second
    ///     list.
    /// </remarks>
    public static ImmutableArray<int> HttpStatuses { get; } =
        [.. All.Select(x => x.HttpStatus).Distinct().Order()];

    /// <summary>Every code that renders as one status, in declaration order.</summary>
    /// <param name="httpStatus">The status.</param>
    public static ImmutableArray<ErrorCode> WithStatus(int httpStatus) =>
        [.. All.Where(x => x.HttpStatus == httpStatus)];

    /// <summary>
    ///     Resolves a wire string back to a registered code. Returns <see langword="false" /> for
    ///     anything not in the registry, including <see langword="null" /> — an unregistered code
    ///     arriving over the wire is data from an older or newer peer, not a code.
    /// </summary>
    public static bool TryFromValue(string? value, [NotNullWhen(true)] out ErrorCode? code) {
        if (value is null) {
            code = null;
            return false;
        }

        return ByValue.TryGetValue(value, out code);
    }

    /// <inheritdoc />
    public bool Equals(ErrorCode? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override bool Equals(object? obj) => ReferenceEquals(this, obj);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Reference equality, which for this closed set is value equality.</summary>
    public static bool operator ==(ErrorCode? left, ErrorCode? right) => ReferenceEquals(left, right);

    /// <summary>The negation of <see cref="op_Equality" />.</summary>
    public static bool operator !=(ErrorCode? left, ErrorCode? right) => !ReferenceEquals(left, right);
}
