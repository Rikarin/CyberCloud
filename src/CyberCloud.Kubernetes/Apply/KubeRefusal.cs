using k8s.Autorest;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace CyberCloud.Kubernetes.Apply;

/// <summary>
///     One API server refusal, split into the sentence a tenant reads and the sentence an operator
///     reads.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The split is the point of the type.</b> A failed <see cref="Result" />'s message
///         reaches a tenant verbatim — <c>OperationGrain</c> copies it into the operation's progress
///         feed and <c>ResourceGrain</c> into <c>ResourceSnapshot.LastFailure</c>, with no redaction
///         anywhere between. An API server's own message is often the only useful diagnostic there
///         is (a policy's rejection names the field and the rule) and just as often names things a
///         tenant must not see (<c>User "system:serviceaccount:…"</c>, namespaces, node names). One
///         string cannot be both, so this type carries two and
///         <see cref="KubeFailures.Classify" /> decides which half of the API server's text goes
///         where.
///     </para>
///     <para>
///         docs/plan/08 § Errors is the licence for the asymmetry: <i>"No exception details, ever. A
///         stack trace in an error body is an information leak and a support-cost multiplier … the
///         details go to the trace."</i>
///         <see cref="OperatorDetail" /> is the trace's copy.
///     </para>
///     <para>
///         Not a wire type, deliberately. It never crosses a grain boundary — the grain logs
///         <see cref="OperatorDetail" /> and returns a <see cref="Result" /> built from
///         <see cref="Code" /> and <see cref="TenantMessage" />, and <c>Result</c>, <c>Error</c> and
///         <c>ErrorCode</c> already have surrogates in <c>CyberCloud.Core.Contracts</c>. Inventing a
///         serializable refusal type would add a third thing to keep aligned with the <c>[Id(n)]</c>
///         manifest for no gain.
///     </para>
/// </remarks>
public sealed record KubeRefusal {
    /// <summary>The registered code the failure is reported under.</summary>
    public required ErrorCode Code { get; init; }

    /// <summary>The HTTP status the API server answered with.</summary>
    public required int Status { get; init; }

    /// <summary>
    ///     The API server's <c>Status.reason</c> — <c>Forbidden</c>, <c>Invalid</c>,
    ///     <c>NotFound</c> — or the empty string when the body was not a <c>v1.Status</c>.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    ///     What the tenant reads. Names the object, the cluster and the class of refusal, and carries
    ///     the API server's own words only when those words belong to the tenant — see
    ///     <see cref="KubeFailures.Classify" />.
    /// </summary>
    public required string TenantMessage { get; init; }

    /// <summary>
    ///     What the operator's log gets: the status, the reason, the API server's full message and
    ///     every cause it listed. Never returned to a caller.
    /// </summary>
    public required string OperatorDetail { get; init; }
}

/// <summary>
///     Turns an API server's answer into a <see cref="KubeRefusal" />, and says which failures mean
///     the cluster is unreachable rather than unwilling.
/// </summary>
/// <remarks>
///     <para>
///         <b>Everything here was measured against a real k3s 1.35</b> rather than derived from the
///         Kubernetes API conventions, because the conventions are wrong about two of these cases.
///         <c>KubeFailureMappingTests</c> re-measures each one on every run, so a Kubernetes minor
///         that changes a shape breaks a test instead of a customer.
///     </para>
///     <para>
///         ⚠ <b>The <c>404</c> that is not a missing object.</b> Three different things answer
///         <c>404</c> and only one of them means "no such object":
///     </para>
///     <list type="table">
///         <item>
///             <term>The object is absent, and the kind is served</term>
///             <description>
///                 A <c>v1.Status</c> with <c>details.name</c> set —
///                 <c>{"message":"deployments.apps \"x\" not found","details":{"name":"x","group":"apps","kind":"deployments"}}</c>.
///                 Normal, and expected on every create.
///             </description>
///         </item>
///         <item>
///             <term>The <i>version</i> is not served (<c>apps/v9</c>)</term>
///             <description>
///                 A <c>v1.Status</c> with <c>details</c> <b>empty</b> and the generic message
///                 <c>"the server could not find the requested resource"</c>.
///             </description>
///         </item>
///         <item>
///             <term>The <i>group</i> is not served at all (no CRD)</term>
///             <description>
///                 ⚠ Not a <c>v1.Status</c> and not even JSON — the API server's mux answers the
///                 bare text <c>404 page not found</c>. Anything that reads <c>Status.details.kind</c>
///                 to tell these apart reads a field that is not there.
///             </description>
///         </item>
///     </list>
///     <para>
///         So the discriminator is <b><c>details.name</c>, and only that</b>: present means the
///         object is absent, absent means the kind is not served. It is the same shape for a built-in
///         kind and for a custom one, which the tests check both ways round by installing a CRD
///         mid-test. The safe side of the reading is the one taken here: an unrecognised 404 body
///         reports the kind as unserved, so a silent "already gone" is never invented out of a
///         misconfiguration.
///     </para>
///     <para>
///         ⚠ <b>A malformed apply body answers <c>500</c>, not <c>400</c>.</b> Sending
///         <c>.spec.replicas: "lots"</c> to a real API server returns
///         <c>500 failed to create typed patch object (…): .spec.replicas: expected numeric (int or
///         float), got string</c>. Every blanket "5xx is transport" rule therefore reports our own
///         malformed object as "cannot reach your cluster", which is precisely what
///         <c>KubeApiClient.IsTransport</c>'s remarks promise it does not do. That one prefix is
///         carved out, narrowly, and nothing else about 5xx changes.
///     </para>
/// </remarks>
public static class KubeFailures {
    /// <summary>The prefix a server-side apply uses when it cannot type-check the body we sent.</summary>
    /// <remarks>
    ///     Matched as a prefix of <c>Status.message</c> on a <c>500</c> and nowhere else. A genuine
    ///     server fault has to stay retryable, so the carve-out is deliberately the narrowest thing
    ///     that recognises the measured shape.
    /// </remarks>
    public const string TypedPatchFailurePrefix = "failed to create typed patch object";

    /// <summary>
    ///     The phrase the API server uses when an apply changes an immutable field of a live
    ///     <c>StatefulSet</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THIS EXISTS BECAUSE THE REFUSAL DOES NOT NAME THE FIELD THAT CHANGED.</b> The
    ///         message is <c>"spec: Forbidden: updates to statefulset spec for fields other than
    ///         'replicas', 'ordinals', 'template', 'updateStrategy', 'revisionHistoryLimit',
    ///         'persistentVolumeClaimRetentionPolicy' and 'minReadySeconds' are forbidden"</c> —
    ///         a list of what <i>may</i> change and nothing about what did. On an upgraded cluster
    ///         that reads as an unattributable reconcile failure on every stateful resource at once,
    ///         which is a support call rather than a diagnosis.
    ///     </para>
    ///     <para>
    ///         Matched on the message rather than on a status code, because the identification is the
    ///         sentence: the same rule can surface as a <c>422 Invalid</c> or a <c>403 Forbidden</c>
    ///         depending on the path, and neither status distinguishes it from any other refusal.
    ///         Measured against <c>rancher/k3s:v1.35.7-k3s1</c> in <c>ClaimTemplateLabelTests</c>,
    ///         which re-measures it on every run.
    ///     </para>
    /// </remarks>
    public const string ImmutableStatefulSetSpecPhrase = "updates to statefulset spec for fields other than";

    /// <summary>
    ///     Whether a code means the cluster <i>answered</i> — the complement of
    ///     <c>KubeApiClient.Unreachable</c>.
    /// </summary>
    /// <param name="code">The code from a failed fabric <see cref="Result" />.</param>
    /// <remarks>
    ///     ⚠ <b>This is what keeps a refused write from being retried forever.</b> A cluster that has
    ///     not answered in 90 seconds goes <c>Degraded</c>, and docs/plan/09 § Cluster connections
    ///     makes a <c>Degraded</c> cluster's reconciles <i>suspended, not failed</i> — so a
    ///     <c>403</c> folded into the health window would degrade a perfectly healthy cluster, turn
    ///     every subsequent apply into <see cref="ApplyResult.Suspended" />, and leave the operation
    ///     rescheduling against an API server that will refuse it every time. A refusal is the
    ///     cluster answering, clearly, and it has to fail the operation instead.
    /// </remarks>
    public static bool MeansTheClusterAnswered(ErrorCode code) =>
        code == ErrorCode.ResourceNotFound
        || code == ErrorCode.InvalidResourceType
        || code == ErrorCode.InvalidRequestBody
        || code == ErrorCode.PolicyViolation
        || code == ErrorCode.AuthorizationFailed
        || code == ErrorCode.PreconditionFailed
        || code == ErrorCode.Conflict;

    /// <summary>
    ///     Classifies an API server answer, or returns <see langword="null" /> when the exception is
    ///     not one — a transport fault, a timeout, a 5xx that is genuinely the server's.
    /// </summary>
    /// <param name="exception">What the client threw.</param>
    /// <param name="target">The object the call addressed, for the message.</param>
    /// <param name="clusterId">The cluster, for the message.</param>
    /// <param name="verb">What was attempted — <c>apply</c>, <c>read</c>, <c>delete</c>, <c>list</c>.</param>
    /// <remarks>
    ///     ⚠ <b>Which half of the API server's text reaches the tenant is decided per class, by who
    ///     owns the refusal.</b>
    ///     <list type="bullet">
    ///         <item>
    ///             An <b>admission</b> decision (<c>403</c> from Pod Security or a webhook, <c>422</c>
    ///             from validation or a <c>ValidatingAdmissionPolicy</c>) describes the tenant's own
    ///             object against the tenant's own cluster policy, and the message is the whole
    ///             diagnostic — it names the field and the rule. It goes through verbatim. On a BYO
    ///             cluster the author of that message and its reader are the same party; on an
    ///             in-house cluster the policy is ours and its message is written for tenants.
    ///         </item>
    ///         <item>
    ///             An <b>authorization</b> or <b>authentication</b> failure (<c>401</c>, an RBAC
    ///             <c>403</c>) names the <i>platform's</i> principal —
    ///             <c>User "system:serviceaccount:default:probe-nobody" cannot get resource …</c> —
    ///             which is an internal identity and a namespace the tenant never chose. The tenant
    ///             gets a fixed sentence; the text goes to the log only.
    ///         </item>
    ///         <item>
    ///             A <b>body</b> we rendered badly (<c>400</c>, the typed-patch <c>500</c>) is our
    ///             bug. Naming our serializer's complaint to a tenant helps nobody, so it goes to the
    ///             log and the tenant is told the platform is at fault rather than their input.
    ///         </item>
    ///     </list>
    /// </remarks>
    public static KubeRefusal? Classify(
        HttpOperationException exception,
        ObjectRef target,
        Guid clusterId,
        string verb
    ) {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(target);

        if (exception.Response is null) {
            return null;
        }

        var status = (int)exception.Response.StatusCode;
        var body = ApiStatus.Read(exception.Response.Content);
        var cluster = clusterId.ToString("D", CultureInfo.InvariantCulture);

        // ── The one refusal that has to be translated rather than quoted ─────────────────────────
        //
        // ⚠ BEFORE THE STATUS SWITCH, BECAUSE WHAT IDENTIFIES THIS IS THE SENTENCE AND NOT THE CODE.
        // Every other arm below decides on the status and then either quotes the API server or says
        // whose fault it is. This one cannot: the API server's own words list the fields that MAY
        // change and never name the one that did, so quoting them tells an operator a StatefulSet
        // apply was rejected and nothing about why. On a cluster being upgraded to the claim-template
        // labels of ADR-013 it fires on every stateful resource at once — see
        // ImmutableStatefulSetSpecPhrase, and src/Providers/README.md § Labelling a nested claim
        // template for what the labels changed.
        if (body.Message.Contains(ImmutableStatefulSetSpecPhrase, StringComparison.OrdinalIgnoreCase)) {
            return new KubeRefusal {
                // ⚠ Terminal. ReconcileOutcome.IsRetryable lists this among the four refusals that
                // are decided the same way every time they are asked — a live StatefulSet's spec is
                // immutable now and in an hour, and rescheduling turns a migration an operator can
                // perform into an OperationTimeout they cannot read.
                Code = ErrorCode.InvalidRequestBody,
                Status = status,
                Reason = body.Reason,
                // ⚠ THE CLUSTER'S OWN WORDS FIRST, THEN THE TRANSLATION — and keeping the quote is
                // not politeness. The API server's sentence is the only evidence of which rule
                // fired, and a message that replaced it with our paraphrase would make the one test
                // that measures this rule against a real k3s assert our paraphrase instead.
                TenantMessage =
                    $"Cluster {cluster} refused to {verb} {target}: "
                    + (body.Message.Length > 0 ? body.Message : "a live StatefulSet's spec is immutable")
                    + ". ⚠ The cluster lists the fields that MAY change and does not name the one "
                    + "that did. On a cluster upgraded to a platform version that labels volume "
                    + "claim templates the field is spec.volumeClaimTemplates, and that is a "
                    + "migration rather than a fault: the StatefulSet has to be deleted with "
                    + "--cascade=orphan first, which keeps the pods and the PersistentVolumeClaims "
                    + "running, after which the next reconcile recreates the set. Nothing in the "
                    + "platform performs that step. The object was not written and no data was "
                    + "touched.",
                OperatorDetail = Detail()
            };
        }

        return status switch {
            (int)HttpStatusCode.BadRequest => Ours(
                ErrorCode.InvalidRequestBody,
                "the API server rejected the request as malformed"
            ),

            (int)HttpStatusCode.Unauthorized => Credentials(
                "the credentials the platform holds for this cluster were rejected"
            ),

            (int)HttpStatusCode.Forbidden when body.IsAuthorizationDenial => Credentials(
                "the credentials the platform holds for this cluster are not permitted to do this"
            ),

            (int)HttpStatusCode.Forbidden => Admission(ErrorCode.PolicyViolation),

            (int)HttpStatusCode.NotFound when body.NamesAnObject => new KubeRefusal {
                Code = ErrorCode.ResourceNotFound,
                Status = status,
                Reason = body.Reason,
                TenantMessage = $"{target} does not exist in cluster {cluster}.",
                OperatorDetail = Detail()
            },

            (int)HttpStatusCode.NotFound => new KubeRefusal {
                Code = ErrorCode.InvalidResourceType,
                Status = status,
                Reason = body.Reason,
                TenantMessage =
                    $"Cluster {cluster} does not serve {target.Kind.ApiVersion} {target.Kind.Kind} "
                    + $"(as {target.Kind.Plural}), so {target} cannot be {Past(verb)}. The kind is "
                    + "missing from the cluster, not the object: install or upgrade the operator "
                    + "that provides it, then retry.",
                OperatorDetail = Detail()
            },

            (int)HttpStatusCode.Conflict => Ours(
                ErrorCode.Conflict,
                "another writer changed the object first"
            ),

            (int)HttpStatusCode.Gone => Ours(
                ErrorCode.PreconditionFailed,
                "the resourceVersion it was given has been compacted out of the cluster's history"
            ),

            (int)HttpStatusCode.UnprocessableEntity => Admission(ErrorCode.InvalidRequestBody),

            (int)HttpStatusCode.InternalServerError
                when body.Message.StartsWith(TypedPatchFailurePrefix, StringComparison.Ordinal) =>
                Ours(
                    ErrorCode.InvalidRequestBody,
                    "the API server could not type-check the object the platform rendered"
                ),

            // ⚠ 408 and 429 are excluded on purpose and the exclusion is load-bearing. Both are 4xx
            // and both are things KubeApiClient.IsTransport already calls transport, which is what
            // makes them retryable. Classifying them here would turn a rate limit into a terminal
            // failure — the mirror image of the bug this type exists to fix.
            >= 400 and < 500
                and not (int)HttpStatusCode.RequestTimeout
                and not (int)HttpStatusCode.TooManyRequests => Ours(
                ErrorCode.InvalidRequestBody,
                $"it answered HTTP {status.ToString(CultureInfo.InvariantCulture)}, which the fabric "
                + "does not recognise"
            ),

            _ => null
        };

        string Detail() =>
            $"Cluster {cluster} refused to {verb} {target}: HTTP "
            + $"{status.ToString(CultureInfo.InvariantCulture)}"
            + (body.Reason.Length > 0 ? $" {body.Reason}" : string.Empty)
            + $". {(body.Message.Length > 0 ? body.Message : exception.Response!.Content ?? "(no body)")}"
            + (body.Causes.Count > 0 ? ". Causes: " + string.Join("; ", body.Causes) + "." : string.Empty);

        // The API server's own words, which for an admission decision are the whole diagnostic.
        KubeRefusal Admission(ErrorCode code) =>
            new() {
                Code = code,
                Status = status,
                Reason = body.Reason,
                TenantMessage =
                    $"Cluster {cluster} refused to {verb} {target}: "
                    + (body.Message.Length > 0
                        ? body.Message
                        : "the cluster's admission control gave no reason")
                    + ". The object was not written. This is a decision made by the cluster's own "
                    + "admission control, not a fault in the platform — the message above comes "
                    + "from the cluster.",
                OperatorDetail = Detail()
            };

        // A refusal aimed at the platform's identity. The tenant learns the class and nothing else.
        KubeRefusal Credentials(string what) =>
            new() {
                Code = ErrorCode.AuthorizationFailed,
                Status = status,
                Reason = body.Reason,
                TenantMessage =
                    $"Cluster {cluster} refused to {verb} {target} because {what}. The object was "
                    + "not written. Nothing about the resource is wrong; the cluster's connection "
                    + "needs an operator's attention.",
                OperatorDetail = Detail()
            };

        // Our own bug. Say so, and keep the internals in the log.
        KubeRefusal Ours(ErrorCode code, string what) =>
            new() {
                Code = code,
                Status = status,
                Reason = body.Reason,
                TenantMessage =
                    $"Cluster {cluster} refused to {verb} {target} because {what}. The object was "
                    + "not written. This is a fault in the platform rather than in the request, and "
                    + "the detail is in the platform's log.",
                OperatorDetail = Detail()
            };
    }

    static string Past(string verb) =>
        verb switch {
            "apply" => "applied",
            "read" => "read",
            "delete" => "deleted",
            "list" => "listed",
            _ => verb
        };

    /// <summary>
    ///     The parts of a <c>v1.Status</c> the classification turns on, read defensively — the body
    ///     is not always a <c>Status</c> and is not always JSON.
    /// </summary>
    sealed record ApiStatus {
        static readonly ApiStatus Empty = new();

        /// <summary><c>Status.reason</c>, or the empty string.</summary>
        public string Reason { get; private init; } = string.Empty;

        /// <summary><c>Status.message</c>, or the empty string.</summary>
        public string Message { get; private init; } = string.Empty;

        /// <summary>Each <c>details.causes[]</c> rendered as <c>field: message</c>.</summary>
        public List<string> Causes { get; private init; } = [];

        /// <summary>
        ///     Whether <c>details.name</c> is set — the one signal that separates "this object is
        ///     absent" from "this kind is not served". See the remarks on <see cref="KubeFailures" />.
        /// </summary>
        public bool NamesAnObject { get; private init; }

        /// <summary>
        ///     Whether the message is the authorizer's rather than an admission plugin's.
        /// </summary>
        /// <remarks>
        ///     ⚠ Prose matching, and there is no alternative: an RBAC denial and a Pod Security
        ///     denial are both <c>403</c> with <c>reason: Forbidden</c> and identically shaped
        ///     <c>details</c>. The measured strings are
        ///     <c>… is forbidden: User "system:serviceaccount:default:probe-nobody" cannot get
        ///     resource "deployments" …</c> against
        ///     <c>… is forbidden: violates PodSecurity "restricted:latest": …</c>, so the
        ///     authorizer's fingerprint is the <c>User "…" cannot</c> pair that
        ///     <c>apierrors.NewForbidden</c> composes. Both arms are asserted against a real k3s.
        ///     Getting it wrong costs the tenant a less specific message and costs the operator
        ///     nothing, because the log line is the same either way.
        /// </remarks>
        public bool IsAuthorizationDenial =>
            Message.Contains("User \"", StringComparison.Ordinal)
            && Message.Contains("cannot ", StringComparison.Ordinal);

        public static ApiStatus Read(string? content) {
            if (string.IsNullOrWhiteSpace(content)) {
                return Empty;
            }

            JsonDocument document;
            try {
                document = JsonDocument.Parse(content);
            } catch (JsonException) {
                // ⚠ The unserved-group 404 lands here: the body is the literal text
                // "404 page not found". An empty Status is the right reading — it names no object,
                // so the 404 arm reports the kind as unserved.
                return Empty;
            }

            using (document) {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) {
                    return Empty;
                }

                List<string> causes = [];
                var namesAnObject = false;

                if (root.TryGetProperty("details", out var details)
                    && details.ValueKind == JsonValueKind.Object) {
                    namesAnObject = Text(details, "name").Length > 0;

                    if (details.TryGetProperty("causes", out var array)
                        && array.ValueKind == JsonValueKind.Array) {
                        foreach (var cause in array.EnumerateArray()) {
                            var field = Text(cause, "field");
                            var message = Text(cause, "message");
                            causes.Add(field.Length > 0 ? $"{field}: {message}" : message);
                        }
                    }
                }

                return new() {
                    Reason = Text(root, "reason"),
                    Message = Text(root, "message"),
                    Causes = causes,
                    NamesAnObject = namesAnObject
                };
            }
        }

        static string Text(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
    }
}
