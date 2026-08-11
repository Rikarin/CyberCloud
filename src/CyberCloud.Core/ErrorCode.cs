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
///         into <c>build/Build.Architecture.cs</c> is the remaining half of docs/plan/08:184-186 and
///         belongs with that file, not this one.
///     </para>
///     <para>
///         Codes are compared by reference (there is one instance per code) and by
///         <see cref="Value" /> ordinally, which are the same relation.
///     </para>
/// </remarks>
public sealed class ErrorCode : IEquatable<ErrorCode> {
    // ── The registry. Every code below cites the document that requires it. ────────────────────
    // Ordering is alphabetical so that a diff on this block is readable.

    /// <summary>The caller is not permitted to perform this operation. docs/plan/08:20.</summary>
    /// <remarks>
    ///     ⚠ Never returned to a caller who may not read the resource — docs/plan/00:115 requires
    ///     <c>404</c>, not <c>403</c>, because existence is not disclosed. Use
    ///     <see cref="ResourceNotFound" /> there.
    /// </remarks>
    public static readonly ErrorCode AuthorizationFailed = new("AuthorizationFailed");

    /// <summary>A concurrent change lost. docs/plan/06:129 (<c>409</c> on a taken name).</summary>
    public static readonly ErrorCode Conflict = new("Conflict");

    /// <summary>An unexpected fault. Carries no exception detail — docs/plan/08:190.</summary>
    public static readonly ErrorCode InternalError = new("InternalError");

    /// <summary>The <c>api-version</c> is unknown or retired. docs/plan/08:144-148.</summary>
    public static readonly ErrorCode InvalidApiVersion = new("InvalidApiVersion");

    /// <summary>A grain key within a tenant is malformed. ADR-002, docs/plan/02:127-141.</summary>
    /// <remarks>
    ///     Also carried by <c>GrainKeys.NormalizeEmail</c>, whose failure means "no email index key
    ///     can be minted for this string". A user-facing sign-up validator should say so in its own
    ///     words rather than surfacing this code — it names the mechanism, not the field.
    /// </remarks>
    public static readonly ErrorCode InvalidGrainKey = new("InvalidGrainKey");

    /// <summary>The request body failed the type's JSON Schema. docs/plan/08:22.</summary>
    public static readonly ErrorCode InvalidRequestBody = new("InvalidRequestBody");

    /// <summary>A resource id path is malformed. docs/plan/06:34-56.</summary>
    public static readonly ErrorCode InvalidResourceId = new("InvalidResourceId");

    /// <summary>
    ///     A resource group or resource name breaks the naming rules. docs/plan/06:87-90.
    /// </summary>
    /// <remarks>
    ///     The message for this code is the mitigation the document asks for: it names the offending
    ///     value and character, states the rule, and says why the rule is strict.
    /// </remarks>
    public static readonly ErrorCode InvalidResourceName = new("InvalidResourceName");

    /// <summary>A provider namespace or resource type is malformed. docs/plan/08:116-137.</summary>
    public static readonly ErrorCode InvalidResourceType = new("InvalidResourceType");

    /// <summary>The operation was cancelled by a caller. docs/plan/08:108-111.</summary>
    public static readonly ErrorCode OperationCanceled = new("OperationCanceled");

    /// <summary>Another operation holds the resource. docs/plan/03:126 (<c>409</c>).</summary>
    public static readonly ErrorCode OperationInProgress = new("OperationInProgress");

    /// <summary>An operation exceeded its budget. docs/plan/08:77-79 (60 minutes).</summary>
    public static readonly ErrorCode OperationTimeout = new("OperationTimeout");

    /// <summary>Policy evaluation denied the request. docs/plan/08:25.</summary>
    public static readonly ErrorCode PolicyViolation = new("PolicyViolation");

    /// <summary>An <c>If-Match</c> etag did not match. docs/plan/06:202.</summary>
    public static readonly ErrorCode PreconditionFailed = new("PreconditionFailed");

    /// <summary>Reconciliation failed terminally. docs/plan/08:60 (<c>Failed</c>).</summary>
    public static readonly ErrorCode ProvisioningFailed = new("ProvisioningFailed");

    /// <summary>The name is already claimed in this scope. docs/plan/06:129.</summary>
    public static readonly ErrorCode ResourceAlreadyExists = new("ResourceAlreadyExists");

    /// <summary>The resource group does not exist. docs/plan/06:11.</summary>
    public static readonly ErrorCode ResourceGroupNotFound = new("ResourceGroupNotFound");

    /// <summary>
    ///     The resource does not exist <i>or</i> the caller may not see it. docs/plan/00:115.
    /// </summary>
    public static readonly ErrorCode ResourceNotFound = new("ResourceNotFound");

    /// <summary>A lock forbids the write or the delete. docs/plan/06:201.</summary>
    public static readonly ErrorCode ScopeLocked = new("ScopeLocked");

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
    public static readonly ErrorCode SchemaInvalid = new("SchemaInvalid");

    /// <summary>The subscription does not exist. docs/plan/06:10.</summary>
    public static readonly ErrorCode SubscriptionNotFound = new("SubscriptionNotFound");

    /// <summary>The tenant does not exist. docs/plan/06:8.</summary>
    public static readonly ErrorCode TenantNotFound = new("TenantNotFound");

    /// <summary>
    ///     The tenant is <c>Suspended</c> and control-plane writes are rejected. docs/plan/06:155.
    /// </summary>
    public static readonly ErrorCode TenantSuspended = new("TenantSuspended");

    /// <summary>
    ///     A quota meter would be exceeded. docs/plan/08:23 and the worked example at
    ///     docs/plan/08:176-179.
    /// </summary>
    public static readonly ErrorCode QuotaExceeded = new("QuotaExceeded");

    static readonly FrozenDictionary<string, ErrorCode> ByValue =
        All.ToFrozenDictionary(x => x.Value, StringComparer.Ordinal);

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
        ResourceNotFound,
        SchemaInvalid,
        ScopeLocked,
        SubscriptionNotFound,
        TenantNotFound,
        TenantSuspended,
        QuotaExceeded
    ];

    /// <summary>The wire form — the exact string that appears in <c>error.code</c>.</summary>
    public string Value { get; }

    ErrorCode(string value) {
        Value = value;
    }

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
