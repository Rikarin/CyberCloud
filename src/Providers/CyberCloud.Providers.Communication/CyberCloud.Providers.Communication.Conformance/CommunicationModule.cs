using CyberCloud.Communication;
using CyberCloud.Communication.Contracts;
using CyberCloud.Conformance;
using CyberCloud.Core;
using CyberCloud.Core.Resources;
using CyberCloud.Providers.Communication.Contracts;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace CyberCloud.Providers.Communication.Conformance;

/// <summary>
///     <c>CyberCloud.Communication</c>'s grains, as the world the shared suite reads and breaks for
///     this family — one instance per case source, so four suites can run at once.
/// </summary>
/// <remarks>
///     <para>
///         Every reading goes through <see cref="GrainCommunicationControlPlane" /> over the
///         harness's own client — the module's seam, constructed the way the gateway constructs it —
///         and never through a reconciler's <c>ObserveAsync</c>. That is what makes
///         <see cref="MatchesAsync" /> clause 4's ground truth here rather than the reconciler
///         grading itself; <see cref="IConvergedModule" />'s remarks carry the argument.
///     </para>
///     <para>
///         ⚠ <b>One instance per case source, for the reason <c>ConformanceState&lt;TSource&gt;</c>
///         is keyed on one.</b> <see cref="Plane" /> is bound in <see cref="Attach" /> from a cluster
///         that exists only once the harness has deployed it, and the case's <c>CreateReconciler</c>
///         reads it from here when the suite drives a pass directly. Four suites run in parallel,
///         each with its own cluster; one shared instance would have handed one suite another's
///         client. <see cref="Modules" /> holds the four.
///     </para>
/// </remarks>
/// <param name="underAService">
///     Whether the case's type sits under a <c>services</c> ancestor — what decides whether
///     <see cref="Reset" /> has an ancestor to empty.
/// </param>
public sealed class CommunicationModule(bool underAService) : IConvergedModule {
    IGrainFactory? grains;

    /// <summary>
    ///     The seam a directly-driven reconciler holds, bound once the harness has a cluster.
    /// </summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="Attach" /> ran.</exception>
    public ICommunicationControlPlane Plane =>
        new GrainCommunicationControlPlane(
            grains
            ?? throw new InvalidOperationException(
                "The harness has not attached a cluster yet. CommunicationModule.Plane is read by "
                + "the case's CreateReconciler, which the suite calls only after "
                + "ProviderTestCluster.InitializeAsync — reaching it earlier is a harness bug."
            )
        );

    /// <inheritdoc />
    public void ConfigureSilo(ISiloBuilder silo) => silo.AddCyberCloudCommunication();

    /// <inheritdoc />
    public void ConfigureHandlers(IServiceCollection services, IGrainFactory grains) {
        // What the gateway registers: the two client-side seams over a cluster client.
        services.AddSingleton(grains);
        services.AddCyberCloudCommunicationClient();
    }

    /// <inheritdoc />
    public void Attach(IGrainFactory grains) => this.grains = grains;

    /// <inheritdoc />
    /// <remarks>
    ///     Removes every channel kind and releases every manual block on the shared ancestor
    ///     service, and nothing else: a channel kind and a manual block each have one owner, and
    ///     the previous test's resource still owns it — every test in the suppression suite blocks
    ///     the same address from a fresh resource, so without the release the second would be
    ///     refused by the first's leftover, correctly and uselessly. Templates carry their own names
    ///     and cannot collide across tests. ⚠ Only a manual block is released; the suite writes
    ///     nothing stronger, and a release that reached a complaint would be refused by the grain,
    ///     which is the grain being right. ⚠ Blocks on the grain calls, because every reset in the
    ///     suite is synchronous; the test runner has no synchronization context to deadlock against.
    /// </remarks>
    public void Reset() {
        if (grains is null || !underAService) {
            return;
        }

        var ancestor = new ResourceId(
            ConformanceIds.Tenant,
            ConformanceIds.Subscription,
            ConformanceIds.ResourceGroup,
            CommunicationServices.Type,
            ConformanceIds.AncestorName(0),
            Guid.Empty
        );

        var plane = Plane;
        var serviceId = CommunicationServices.ServiceIdOf(ancestor);

        Task.Run(async () => {
                foreach (var spelled in ChannelKinds.AllowedValues) {
                    Throwing(await plane.RemoveChannelAsync(ancestor.TenantId, serviceId, ChannelKinds.Parse(spelled), CancellationToken.None));
                }

                // A list is never not-found: the grain answers empty for a service never created.
                var listed = Throwing(await plane.ListSuppressionsAsync(ancestor.TenantId, serviceId, ChannelKind.Unknown, CancellationToken.None));

                foreach (var entry in listed.Where(x => x.Reason == SuppressionReason.ManualBlock)) {
                    Throwing(
                        await plane.ReleaseSuppressionAsync(
                            ancestor.TenantId,
                            serviceId,
                            entry.Channel,
                            entry.Destination,
                            "released between tests by the conformance suite",
                            CancellationToken.None
                        )
                    );
                }
            }
        ).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async Task<bool> HoldsAsync(ResourceId id, CancellationToken cancellationToken) {
        var plane = Plane;
        var serviceId = CommunicationServices.ServiceIdOf(id);

        switch (id.Type.Type) {
            case CommunicationServices.TypePath:
                return (await plane.DescribeServiceAsync(id.TenantId, serviceId, cancellationToken)).IsSuccess;

            case CommunicationChannels.TypePath: {
                // The kind is in the body, and Holds is asked without one. Every configured kind is
                // read and the one this resource owns is the answer.
                var described = await plane.DescribeServiceAsync(id.TenantId, serviceId, cancellationToken);
                return described.TryGetValue(out var service)
                    && !service.Channels.IsDefault
                    && service.Channels.Any(x => x.OwnerResourceId == id.Id);
            }

            case CommunicationTemplates.TypePath: {
                var resolved = await plane.ResolveTemplateAsync(id.TenantId, serviceId, id.Name, cancellationToken);
                return resolved.TryGetValue(out var templateId) && templateId == CommunicationTemplates.TemplateIdOf(id);
            }

            case CommunicationSuppressions.TypePath: {
                // A manual block is the only entry a resource can own, and the entry names its owner;
                // Holds asks for the resource's contribution, and without a body the list is the only
                // place to look for it.
                var listed = await plane.ListSuppressionsAsync(id.TenantId, serviceId, ChannelKind.Unknown, cancellationToken);
                return listed.TryGetValue(out var entries)
                    && !entries.IsDefault
                    && entries.Any(x => x.Reason == SuppressionReason.ManualBlock && x.OwnerResourceId == id.Id);
            }

            default:
                throw Unknown(id);
        }
    }

    /// <inheritdoc />
    public async Task<bool> MatchesAsync(ResourceId id, string desiredJson, CancellationToken cancellationToken) {
        var plane = Plane;
        var serviceId = CommunicationServices.ServiceIdOf(id);
        using var desired = JsonDocument.Parse(desiredJson);
        var body = desired.RootElement;

        switch (id.Type.Type) {
            case CommunicationServices.TypePath: {
                var described = await plane.DescribeServiceAsync(id.TenantId, serviceId, cancellationToken);
                return described.TryGetValue(out var service) && CommunicationServices.Matches(service, id, body);
            }

            case CommunicationChannels.TypePath: {
                var held = await plane.GetChannelAsync(id.TenantId, serviceId, CommunicationChannels.KindOf(body), cancellationToken);
                return held.TryGetValue(out var configuration) && CommunicationChannels.Matches(configuration, id, body);
            }

            case CommunicationTemplates.TypePath: {
                var resolved = await plane.ResolveTemplateAsync(id.TenantId, serviceId, id.Name, cancellationToken);
                if (!resolved.TryGetValue(out var templateId) || templateId != CommunicationTemplates.TemplateIdOf(id)) {
                    return false;
                }

                var versions = await plane.ListTemplateVersionsAsync(id.TenantId, templateId, cancellationToken);
                return versions.TryGetValue(out var list)
                    && !list.IsDefaultOrEmpty
                    && CommunicationTemplates.Matches(list[^1], body);
            }

            case CommunicationSuppressions.TypePath: {
                var check = await plane.CheckSuppressionAsync(
                    id.TenantId,
                    serviceId,
                    CommunicationSuppressions.ChannelOf(body),
                    CommunicationSuppressions.DestinationOf(body),
                    cancellationToken
                );

                return check.TryGetValue(out var result)
                    && result is { IsSuppressed: true, Entry: { } entry }
                    && CommunicationSuppressions.Matches(entry, id, body);
            }

            default:
                throw Unknown(id);
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(ResourceId id, string desiredJson, CancellationToken cancellationToken) {
        var plane = Plane;
        var serviceId = CommunicationServices.ServiceIdOf(id);
        using var desired = JsonDocument.Parse(desiredJson);
        var body = desired.RootElement;

        switch (id.Type.Type) {
            case CommunicationServices.TypePath:
                Throwing(await plane.RetireServiceAsync(id.TenantId, serviceId, cancellationToken));
                break;

            case CommunicationChannels.TypePath:
                Throwing(await plane.RemoveChannelAsync(id.TenantId, serviceId, CommunicationChannels.KindOf(body), cancellationToken));
                break;

            case CommunicationTemplates.TypePath:
                Throwing(
                    await plane.UnregisterTemplateAsync(
                        id.TenantId,
                        serviceId,
                        id.Name,
                        CommunicationTemplates.TemplateIdOf(id),
                        cancellationToken
                    )
                );

                break;

            case CommunicationSuppressions.TypePath:
                Throwing(
                    await plane.ReleaseSuppressionAsync(
                        id.TenantId,
                        serviceId,
                        CommunicationSuppressions.ChannelOf(body),
                        CommunicationSuppressions.DestinationOf(body),
                        "removed behind the reconciler's back by the conformance suite",
                        cancellationToken
                    )
                );

                break;

            default:
                throw Unknown(id);
        }
    }

    /// <inheritdoc />
    public async Task CorruptAsync(ResourceId id, string desiredJson, CancellationToken cancellationToken) {
        var plane = Plane;
        var serviceId = CommunicationServices.ServiceIdOf(id);
        using var desired = JsonDocument.Parse(desiredJson);
        var body = desired.RootElement;

        switch (id.Type.Type) {
            case CommunicationServices.TypePath:
                // A locale no body in the suite asks for.
                Throwing(await plane.EnsureServiceAsync(id.TenantId, serviceId, id.Name, "zu-ZA", cancellationToken));
                break;

            case CommunicationChannels.TypePath: {
                var held = Throwing(await plane.GetChannelAsync(id.TenantId, serviceId, CommunicationChannels.KindOf(body), cancellationToken));

                Throwing(
                    await plane.ConfigureChannelAsync(
                        id.TenantId,
                        serviceId,
                        held with { Limits = held.Limits with { MaxMessagesPerWindow = held.Limits.MaxMessagesPerWindow + 7919 } },
                        cancellationToken
                    )
                );

                break;
            }

            case CommunicationTemplates.TypePath:
                // A version nobody asked for, on top — the newest is what a send would use.
                Throwing(
                    await plane.AddTemplateVersionAsync(
                        id.TenantId,
                        CommunicationTemplates.TemplateIdOf(id),
                        [],
                        [new() { Locale = "zu-ZA", Subject = "edited by hand", Body = "edited by hand" }],
                        cancellationToken
                    )
                );

                break;

            case CommunicationSuppressions.TypePath:
                // ⚠ Unowned, like a hand edit: nobody's resource wrote it, and the next pass adopts
                // it — an owned corruption would be a second resource's, which the pass refuses
                // rather than overwrites, and the suite would read the refusal as a failed repair.
                Throwing(
                    await plane.SuppressAsync(
                        id.TenantId,
                        serviceId,
                        CommunicationSuppressions.ChannelOf(body),
                        CommunicationSuppressions.DestinationOf(body),
                        SuppressionReason.ManualBlock,
                        "edited by hand",
                        Guid.Empty,
                        cancellationToken
                    )
                );

                break;

            default:
                throw Unknown(id);
        }
    }

    static InvalidOperationException Unknown(ResourceId id) =>
        new($"'{id.Type}' is not a type CommunicationModule knows how to read. The four it does are declared in CommunicationProvider.");

    static void Throwing(Result result) {
        if (result.TryGetError(out var error)) {
            throw new InvalidOperationException($"The module refused a write the suite made behind the reconciler's back: {error.Message}");
        }
    }

    static T Throwing<T>(Result<T> result)
        where T : notnull {
        if (result.TryGetError(out var error)) {
            throw new InvalidOperationException($"The module refused a call the suite made around the reconciler: {error.Message}");
        }

        return result.GetValueOrThrow();
    }
}

/// <summary>The four instances, one per case source. See <see cref="CommunicationModule" />.</summary>
public static class Modules {
    /// <summary>For <c>CommunicationServiceCase</c>.</summary>
    public static CommunicationModule Service { get; } = new(underAService: false);

    /// <summary>For <c>CommunicationChannelCase</c>.</summary>
    public static CommunicationModule Channel { get; } = new(underAService: true);

    /// <summary>For <c>CommunicationTemplateCase</c>.</summary>
    public static CommunicationModule Template { get; } = new(underAService: true);

    /// <summary>For <c>CommunicationSuppressionCase</c>.</summary>
    public static CommunicationModule Suppression { get; } = new(underAService: true);
}
