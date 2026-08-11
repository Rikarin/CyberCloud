using Microsoft.CodeAnalysis;

namespace CyberCloud.Analyzers;

/// <summary>
///     Every diagnostic this assembly can report, and the plan claim each one makes true.
/// </summary>
/// <remarks>
///     <para>
///         <b>The id space.</b> <c>CC1000</c>–<c>CC1999</c> are analyzer diagnostics. <c>CC0001</c>
///         is already taken by an MSBuild error in <c>Directory.Build.targets</c>
///         (<c>CyberCloudGuardAgainstMultiTargeting</c>), so the analyzer range deliberately starts
///         at 1000 rather than 1 — two things emitting <c>CC0002</c> from different layers would be
///         unsearchable.
///     </para>
///     <para>
///         <b>Severity is not chosen here.</b> Every descriptor below defaults to
///         <see cref="DiagnosticSeverity.Warning" />, and <c>.editorconfig</c> is where the
///         repository decides what each one actually is. That matters more here than in most
///         repositories: <c>Directory.Build.props</c> sets <c>TreatWarningsAsErrors</c>, so
///         <i>warning and error are the same outcome</i> — a broken build — and the only real choice
///         is between "breaks the build" (<c>warning</c>/<c>error</c>) and "does not"
///         (<c>suggestion</c>/<c>silent</c>). Each rule's entry in <c>.editorconfig</c> records
///         which, and why.
///     </para>
/// </remarks>
public static class Rules {
    /// <summary>
    ///     CC1001 — <c>.Result</c>, <c>.Wait()</c> or <c>.GetAwaiter().GetResult()</c> on a task from
    ///     a method that is not <c>async</c>.
    /// </summary>
    /// <remarks>
    ///     docs/plan/00 § Coding standards:
    ///     <i>
    ///         "No <c>async void</c>, no <c>.Result</c>, no
    ///         <c>.Wait()</c> — analyzer-enforced. Grain code is single-threaded per activation and
    ///         blocking it deadlocks the silo."
    ///     </i>
    ///     ⚠ <c>CA1849</c>, which <c>.editorconfig</c> already
    ///     has on as an error, fires only when the blocking call sits inside an <i>already-</i>
    ///     <c>async</c> method. That is not the shape that deadlocks a silo. This rule is the other
    ///     half and deliberately does <b>not</b> overlap: it fires only in a synchronous context.
    /// </remarks>
    public static readonly DiagnosticDescriptor BlockingWaitInSynchronousMethod = new(
        Ids.BlockingWaitInSynchronousMethod,
        "Do not block on a task from a synchronous method",
        "'{0}' blocks on a task from synchronous member '{1}'. Make '{1}' async and await it — "
        + "grain code is single-threaded per activation and blocking it deadlocks the silo "
        + "(docs/plan/00 § Coding standards). CA1849 does not cover this shape.",
        Categories.Async,
        DiagnosticSeverity.Warning,
        true,
        "CA1849 only reports a blocking call inside an already-async method. A synchronous method "
        + "that calls task.Result, task.Wait() or task.GetAwaiter().GetResult() is the shape that "
        + "deadlocks an Orleans activation, and nothing shipping with the SDK reports it."
    );

    /// <summary>CC1002 — an <c>async void</c> method, local function or lambda.</summary>
    /// <remarks>
    ///     docs/plan/00 § Coding standards. ⚠ There is no built-in rule: the one people reach for is
    ///     <c>VSTHRD100</c>, which lives in <c>Microsoft.VisualStudio.Threading.Analyzers</c> — a
    ///     package this repository deliberately does not reference, because it arrives with twenty
    ///     other opinions.
    /// </remarks>
    public static readonly DiagnosticDescriptor AsyncVoid = new(
        Ids.AsyncVoid,
        "Do not declare an async void member",
        "'{0}' is async void. Return Task — an exception thrown after the first await is raised on "
        + "the thread pool and terminates the process, and nothing can await completion "
        + "(docs/plan/00 § Coding standards).",
        Categories.Async,
        DiagnosticSeverity.Warning,
        true,
        "An async void member cannot be awaited and cannot be caught: its exceptions are raised on "
        + "whatever thread the continuation lands on. docs/plan/00 § Coding standards bans it "
        + "outright."
    );

    /// <summary>CC1003 — a <c>[GenerateSerializer]</c> type with no <c>[Alias]</c>.</summary>
    /// <remarks>
    ///     docs/plan/00 § Coding standards and docs/plan/04 § Failure and upgrade: a rolling silo
    ///     upgrade works because the alias, not the CLR name, is what a silo of version N looks up
    ///     when a silo of version N+1 sends it a payload. This was previously a reflection test over
    ///     one assembly (<c>WireContractTests.EveryGenerateSerializerTypeHasAnAlias</c>); as a
    ///     compile-time rule it covers every assembly the analyzer is referenced by, including grain
    ///     state types that live outside a <c>.Contracts</c> assembly.
    /// </remarks>
    public static readonly DiagnosticDescriptor GenerateSerializerWithoutAlias = new(
        Ids.GenerateSerializerWithoutAlias,
        "A [GenerateSerializer] type needs a stable [Alias]",
        "'{0}' has [GenerateSerializer] but no [Alias]. A rolling silo upgrade resolves types by "
        + "alias, so renaming this type would become a wire break "
        + "(docs/plan/04 § Failure and upgrade).",
        Categories.Serialization,
        DiagnosticSeverity.Warning,
        true,
        "Orleans resolves a payload's type by its [Alias] when one is present and by its CLR name "
        + "when it is not. Without an alias a rename is a silent wire break between silo versions."
    );

    /// <summary>CC1004 — a hand-built grain key: a string literal containing <c>|</c>.</summary>
    /// <remarks>
    ///     docs/plan/02 § ADR-002:
    ///     <i>
    ///         "Nothing else in the codebase may concatenate a grain key,
    ///         enforced by an analyzer that flags string literals containing <c>|</c> in
    ///         <c>GetGrain</c> arguments."
    ///     </i>
    ///     <c>|</c> is <c>Orleans.Multitenant</c>'s tenant separator;
    ///     a literal containing one is either a forged tenant qualification or a key that will be
    ///     parsed as one.
    /// </remarks>
    public static readonly DiagnosticDescriptor GrainKeyLiteral = new(
        Ids.GrainKeyLiteral,
        "A grain key is built by GrainKeys, never by a literal containing the tenant separator",
        "The key passed to '{0}' is a literal containing '|', which is Orleans.Multitenant's tenant "
        + "separator. Build the key with CyberCloud.Core.Resources.GrainKeys and qualify it with "
        + "ForTenant — docs/plan/02 § ADR-002.",
        Categories.Tenancy,
        DiagnosticSeverity.Warning,
        true,
        "Orleans.Multitenant encodes the tenant into the physical grain key as "
        + "{tenantId}|{keyWithinTenant}. A literal key containing '|' either forges that "
        + "qualification or collides with it. GrainKeys is the one type allowed to format a key."
    );

    /// <summary>CC1005 — a secret-shaped name on an <c>[Id]</c>-annotated member.</summary>
    /// <remarks>
    ///     docs/plan/00 § Non-negotiables, the "Secrets never reach grain state" row:
    ///     <i>
    ///         "Analyzer bans <c>[Id]</c>-annotated members named <c>*Password</c>, <c>*Secret</c>,
    ///         <c>*Token</c>, <c>*Key</c> outside <c>CyberCloud.Vault</c>; secrets are <c>SecretRef</c>
    ///         handles resolved at the data plane."
    ///     </i>
    /// </remarks>
    public static readonly DiagnosticDescriptor SecretInGrainState = new(
        Ids.SecretInGrainState,
        "A secret must not be a serialized member of grain state",
        "'{0}' is an [Id]-annotated member whose name ends in '{1}', so it serializes into grain "
        + "state. Store a SecretRef handle and resolve it at the data plane "
        + "(docs/plan/00 § Non-negotiables). Only CyberCloud.Vault may hold the value.",
        Categories.Security,
        DiagnosticSeverity.Warning,
        true,
        "An [Id(n)] member is written to the durable tier and travels on the wire between silos. A "
        + "secret that reaches grain state is a secret in every backup, every replica and every "
        + "serialization trace of that grain."
    );

    /// <summary>CC1006 — a raw <c>IGrainFactory.GetGrain</c> from code that is not a grain.</summary>
    /// <remarks>
    ///     <para>
    ///         docs/plan/00 § Non-negotiables, the corrected tenant-separation row, and docs/plan/10.
    ///         <c>Orleans.Multitenant</c>'s <c>TenantSeparatingCallFilter</c> consults the authorizer
    ///         only when the call has a source grain that is neither a client nor a system target, so
    ///         a caller that is not a grain is outside it <i>by construction</i> — see
    ///         <c>CyberCloud.Tenancy.Tests.CrossTenantReachabilityTests.Route7b_FromOutsideAGrainTheRawKeyIsSTILLOPEN</c>,
    ///         which proves the hole is open with separation fully wired.
    ///     </para>
    ///     <para>
    ///         The gateway is an Orleans <i>client</i> by design (docs/plan/03, docs/plan/10), so it
    ///         can never be inside that filter. This rule is the gate the plan says is missing.
    ///     </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor UnqualifiedGrainReference = new(
        Ids.UnqualifiedGrainReference,
        "Outside grain code, take a grain reference through ForTenant",
        "'{0}' calls IGrainFactory.GetGrain directly and is not grain code. Orleans.Multitenant's "
        + "call filter never sees a caller that is not a grain, so this reference is outside tenant "
        + "separation — use IGrainFactory.ForTenant(tenantId).GetGrain(...), or a GrainKeys "
        + "null-tenant key for a platform grain (docs/plan/00 § Non-negotiables, docs/plan/10).",
        Categories.Security,
        DiagnosticSeverity.Warning,
        true,
        "TenantSeparatingCallFilter attributes an incoming call to the calling grain's tenant and "
        + "returns without consulting ICrossTenantAuthorizer when there is no source grain, or when "
        + "the source is a client or a system target. Anything holding an IGrainFactory outside a "
        + "grain — the gateway, the CLI, a hosted service — is therefore permanently outside it, "
        + "and must establish the tenant itself with ForTenant."
    );

    /// <summary>CC1007 — <c>#pragma warning disable</c> with no linked issue.</summary>
    /// <remarks>
    ///     docs/plan/00 § Non-negotiables, the "Warnings are errors" row:
    ///     <i>
    ///         "no
    ///         <c>#pragma warning disable</c> without a linked issue"
    ///     </i>
    ///     . Nothing built in reports this.
    /// </remarks>
    public static readonly DiagnosticDescriptor PragmaWithoutIssue = new(
        Ids.PragmaWithoutIssue,
        "A #pragma warning disable needs a linked issue",
        "This '#pragma warning disable {0}' has no linked issue. Put a URL, a '#123' or a "
        + "'PROJ-123' in a comment on the same line or the line above "
        + "(docs/plan/00 § Non-negotiables).",
        Categories.Hygiene,
        DiagnosticSeverity.Warning,
        true,
        "A suppression with no link is permanent by default: nobody can tell whether it is a "
        + "known-and-tracked compromise or a warning somebody silenced on a Friday."
    );

    /// <summary>Analyzer diagnostic categories, one per plan concern.</summary>
    public static class Categories {
        /// <summary>Blocking, <c>async void</c>, and everything else that stalls an activation.</summary>
        public const string Async = "CyberCloud.Async";

        /// <summary>Wire-contract shape — <c>[GenerateSerializer]</c>, <c>[Alias]</c>, <c>[Id(n)]</c>.</summary>
        public const string Serialization = "CyberCloud.Serialization";

        /// <summary>Grain keys and tenant qualification.</summary>
        public const string Tenancy = "CyberCloud.Tenancy";

        /// <summary>The non-negotiables that are security properties rather than style.</summary>
        public const string Security = "CyberCloud.Security";

        /// <summary>Repository hygiene rules from docs/plan/00 § Non-negotiables.</summary>
        public const string Hygiene = "CyberCloud.Hygiene";
    }

    /// <summary>The diagnostic ids, as constants, so a suppression can be grepped back to a rule.</summary>
    public static class Ids {
        /// <summary><see cref="BlockingWaitInSynchronousMethod" />.</summary>
        public const string BlockingWaitInSynchronousMethod = "CC1001";

        /// <summary><see cref="AsyncVoid" />.</summary>
        public const string AsyncVoid = "CC1002";

        /// <summary><see cref="GenerateSerializerWithoutAlias" />.</summary>
        public const string GenerateSerializerWithoutAlias = "CC1003";

        /// <summary><see cref="GrainKeyLiteral" />.</summary>
        public const string GrainKeyLiteral = "CC1004";

        /// <summary><see cref="SecretInGrainState" />.</summary>
        public const string SecretInGrainState = "CC1005";

        /// <summary><see cref="UnqualifiedGrainReference" />.</summary>
        public const string UnqualifiedGrainReference = "CC1006";

        /// <summary><see cref="PragmaWithoutIssue" />.</summary>
        public const string PragmaWithoutIssue = "CC1007";
    }
}
