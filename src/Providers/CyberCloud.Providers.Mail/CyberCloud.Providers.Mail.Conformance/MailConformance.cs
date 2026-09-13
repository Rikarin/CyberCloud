using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Providers.Mail.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Mail.Conformance;

/// <summary>
///     <c>CyberCloud.Mail/domains</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             THIS FILE IS THE ENTIRE COST OF PUTTING THE FOURTEENTH PROVIDER UNDER CONFORMANCE,
///             AND IT IS THE FIRST WHOSE OBJECTS THE CASE CANNOT PREDICT THE BYTES OF.
///         </b>
///         <c>test/CyberCloud.Conformance</c> was not touched. What is new is the <c>Secret</c>: its
///         contents are read back out of the vault rather than computed from the body, so a case that
///         compared documents byte for byte could not have described it.
///         <see cref="MailDomains.Matches" /> answers <see langword="true" /> for a <c>Secret</c>,
///         and that is honest rather than lazy — for an object no controller rewrites, an apply that
///         landed and read back <i>is</i> the whole claim.
///     </para>
///     <para>
///         ⚠ <b>SO STATE PLAINLY WHAT A GREEN RUN HERE PROVES AND WHAT IT DOES NOT.</b> It proves the
///         twelve-step write path, the verb grammar, the four reconciler clauses, the cross-tenant
///         404, the seven labels and the delete-read-back, over five objects. It proves
///         <b>
///             nothing at
///             all
///         </b> about whether mail is accepted, delivered, signed, filtered or readable — the
///         images those three containers would run <b>do not exist</b>
///         (<c>charts/managed/mail/conformance.yaml § owed</c>, <c>the-images-do-not-exist</c>), so
///         no harness in this repository can start one. ⚠ It also proves nothing about the one
///         property that matters most on this type, <b>that the DKIM key never changes</b>: that is a
///         hand-written test in <c>CyberCloud.Providers.Mail.Tests</c>, because it needs two passes
///         over one vault and the shared suite drives a single create.
///     </para>
/// </remarks>
public sealed class MailDomainCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Mail/domains",
            CreateProvider = () => new MailProvider(),
            ReconcilerType = typeof(MailDomainReconciler),
            CreateReconciler = clock => new MailDomainReconciler(clock),
            Type = MailDomains.Type,
            ApiVersion = MailDomains.V2026,
            Body = cluster => MailDomains.Body(cluster),
            // ⚠ Changes `storage.size`, which the reconciler renders into the claim template and
            // MailDomains.Matches reads back off the StatefulSet. Two other candidates were wrong for
            // two different reasons, and both look right:
            //
            //   * `filtering.rejectThreshold` reaches only the ConfigMap, whose Matches is `true` by
            //     construction — so the update assertion would pass over a reconciler that ignored
            //     the change entirely.
            //   * `sieve` DOES change the Service's port count and would work here, but it changes
            //     the ConfigMap too, so a failure would not say which document was wrong.
            ChangedBody = cluster => MailDomains.Body(cluster, storageSize: "40Gi"),
            // Drops the required `/properties/storage/size`.
            // ⚠ Built from a valid body with one required property removed rather than hand-written: a
            // hand-written invalid body drifts out of date the day the schema gains a property and
            // then tests "invalid for the wrong reason" while still going green.
            InvalidBody = cluster => WithoutStorageSize(MailDomains.Body(cluster)),
            InvalidBodyTarget = "/properties/storage/size",
            // ⚠ EXPLICITLY EMPTY, AND THIS IS THE ONLY CASE IN THE TREE THAT SAYS SO. This type
            // declares no action at all — MailProvider carries the argument, which is that
            // actions-without-handlers.txt permits a handler-less action only on an ALREADY published
            // api-version, and this type's is published by the same change that would declare one.
            // The suite's two POST assertions skip loudly on an empty name rather than passing.
            //
            // ⚠ WRITTEN OUT RATHER THAN DEFAULTED, AND THE DIFFERENCE IS THE WHOLE POINT. Making the
            // member optional would have let EVERY case omit it — including one that has an action
            // and forgot — and the suite would have stopped asserting the verb grammar for that
            // provider with nothing to say so. ReferenceConformance's
            // EveryCaseFieldIsRequiredSoAPartialRegistrationDoesNotCompile caught exactly that
            // attempt. One explicit line here is the cost of keeping the accident impossible.
            ActionName = string.Empty,
            Objects = (id, ns) => MailDomains.Objects(ns, id.Name),
            // ⚠ NOTHING. There is no operator for any of the three components, so no controller
            // writes an object this provider reads back. Stated rather than defaulted — see
            // ProviderConformanceCase.OperatorWritten.
            OperatorWritten = static (_, _) => [],
            ObjectMatchesDesired = match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);

                return MailDomains.Matches(match.ObjectJson, desired.RootElement);
            }
        };

    /// <summary>A valid body with the required mail volume size removed.</summary>
    /// <param name="body">A valid body.</param>
    static string WithoutStorageSize(string body) {
        var node = JsonNode.Parse(body)!.AsObject();

        node["properties"]!.AsObject()["storage"]!.AsObject().Remove("size");

        return node.ToJsonString();
    }
}

/// <summary>The shared suite, run against the managed-mail provider.</summary>
/// <param name="cluster">The harness.</param>
public sealed class MailDomainConformance(ProviderTestCluster<MailDomainCase> cluster)
    : ProviderConformanceTests<MailDomainCase>(cluster), IClassFixture<ProviderTestCluster<MailDomainCase>>;

/// <summary>The container-backed half, skipped loudly, against the managed-mail provider.</summary>
/// <remarks>
///     ⚠
///     <b>
///         Still declared, even though this provider has a real <c>*.Cluster.Conformance</c> project
///         that makes the same assertions against a real API server.
///     </b> The skips are not redundant with
///     it: they run on a machine with no Docker and say, by name, which criteria were not checked.
///     Deleting them here would make "conformance: green" readable as "the cluster-backed criteria
///     were met" on exactly the machines where they were not.
/// </remarks>
public sealed class MailDomainClusterBackedConformance() : ClusterBackedConformanceTests(MailDomainCase.ProviderCase);
