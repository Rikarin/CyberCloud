using CyberCloud.Core.Time;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Communication;

/// <summary>
///     Converges one <c>services/templates</c> resource onto its <c>IMessageTemplateGrain</c> and
///     its name on the service.
/// </summary>
/// <remarks>
///     <para>
///         The same four clauses <see cref="CommunicationServiceReconciler" /> lists. The one worth
///         a paragraph is idempotence, because the grain is append-only: a pass appends a version
///         <b>only when the newest one differs from the body</b>, compared through
///         <see cref="CommunicationTemplates.Matches" />. Without that check every drift scan would
///         add a version, and a carrier-approved WhatsApp template would be buried under a hundred
///         identical drafts by the end of the week.
///     </para>
///     <para>
///         ⚠ <b>The name is claimed on the service before the grain is written, and a name another
///         template holds is <see cref="ErrorCode.Conflict" />.</b> <c>ICommunicationServiceGrain</c>
///         is the naming authority; a send resolves the name there in one hop. A template that
///         existed under a name it did not own would be a template a send could never reach, which
///         converges and does nothing.
///     </para>
///     <para>
///         ⚠ <b>The delete forgets the name and keeps the versions.</b> The grain is keyed by the
///         resource's address, so a template recreated under the same name continues its history; a
///         carrier's approval is attached to a body and the body is evidence
///         (<c>IMessageTemplateGrain</c>'s remarks). Converged once the name no longer resolves to
///         this template, read back.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
/// <param name="plane">The module.</param>
public sealed class CommunicationTemplateReconciler(IClock clock, ICommunicationControlPlane plane) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => CommunicationTemplates.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        var channel = CommunicationTemplates.ChannelOf(context.Desired);
        if (channel == ChannelKind.Unknown) {
            return ReconcileOutcome.Failed(
                ErrorCode.InvalidRequestBody,
                "The template's channel is not one of " + string.Join(", ", ChannelKinds.AllowedValues) + "."
            );
        }

        var tenantId = context.Id.TenantId;
        var serviceId = CommunicationServices.ServiceIdOf(context.Id);
        var templateId = CommunicationTemplates.TemplateIdOf(context.Id);

        context.Log.Report("registering", $"claiming the template name '{context.Id.Name}' on the service", 30);

        var ensured = await plane.EnsureTemplateAsync(tenantId, serviceId, templateId, context.Id.Name, channel, cancellationToken);
        if (ensured.TryGetError(out var ensureError)) {
            if (ensureError.Code == ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.InProgress(
                    $"the service '{context.Id.ParentNames}' is not provisioned yet; the template waits for it",
                    TimeSpan.FromSeconds(10)
                );
            }

            return ensureError.Code == ErrorCode.ResourceAlreadyExists
                ? ReconcileOutcome.Failed(ErrorCode.Conflict, ensureError.Message)
                : ReconcileOutcome.FromFailure(ensureError);
        }

        if (ensured.GetValueOrThrow().Channel != channel) {
            // ⚠ The grain at this address was created for another channel — the resource was deleted
            // and recreated with a different one. A channel is immutable on a template because a
            // WhatsApp approval is worthless on email; the honest answer is a new name.
            return ReconcileOutcome.Failed(
                ErrorCode.Conflict,
                $"The template '{context.Id.Name}' was created for {ChannelKinds.Spell(ensured.GetValueOrThrow().Channel)} "
                + $"and this body says {ChannelKinds.Spell(channel)}. A template's channel is immutable and its "
                + "version history follows its name, so a template for another channel needs another name."
            );
        }

        var versions = await plane.ListTemplateVersionsAsync(tenantId, templateId, cancellationToken);
        if (versions.TryGetError(out var listError)) {
            return ReconcileOutcome.FromFailure(listError);
        }

        if (Newest(versions.GetValueOrThrow()) is { } current && CommunicationTemplates.Matches(current, context.Desired)) {
            context.Log.Report("ready", $"version {current.Version} of '{context.Id.Name}' already carries the desired body", 100);
            return ReconcileOutcome.Converged;
        }

        context.Log.Report("versioning", $"appending a version of '{context.Id.Name}'", 60);

        var added = await plane.AddTemplateVersionAsync(
            tenantId,
            templateId,
            CommunicationTemplates.ParametersOf(context.Desired),
            [CommunicationTemplates.BodyOf(context.Desired)],
            cancellationToken
        );

        if (added.TryGetError(out var addError)) {
            return ReconcileOutcome.FromFailure(addError);
        }

        // ── Clause 4. ───────────────────────────────────────────────────────────────────────────
        var read = await plane.ListTemplateVersionsAsync(tenantId, templateId, cancellationToken);
        if (read.TryGetError(out var readError)) {
            return ReconcileOutcome.FromFailure(readError);
        }

        if (Newest(read.GetValueOrThrow()) is not { } newest) {
            return ReconcileOutcome.InProgress("the version was appended and does not read back yet", TimeSpan.FromSeconds(5));
        }

        if (newest.Channel != channel) {
            // The grain stamps every version with the channel it was CREATED for, so a grain that
            // was created for another channel with no version yet slips past the check above and
            // shows up here. Same answer, one pass later.
            return ReconcileOutcome.Failed(
                ErrorCode.Conflict,
                $"The template '{context.Id.Name}' is written for {ChannelKinds.Spell(newest.Channel)} and this "
                + $"body says {ChannelKinds.Spell(channel)}. A template's channel is immutable; use another name."
            );
        }

        if (!CommunicationTemplates.Matches(newest, context.Desired)) {
            return ReconcileOutcome.InProgress("the version was appended and does not read back as the newest yet", TimeSpan.FromSeconds(5));
        }

        context.Log.Report("ready", $"version {newest.Version} of '{context.Id.Name}' reads back as desired", 100);
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        var tenantId = context.Id.TenantId;
        var serviceId = CommunicationServices.ServiceIdOf(context.Id);
        var templateId = CommunicationTemplates.TemplateIdOf(context.Id);

        context.Log.Report("unregistering", $"forgetting the template name '{context.Id.Name}' on the service");

        var unregistered = await plane.UnregisterTemplateAsync(tenantId, serviceId, context.Id.Name, templateId, cancellationToken);
        if (unregistered.TryGetError(out var error) && error.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(error);
        }

        var resolved = await plane.ResolveTemplateAsync(tenantId, serviceId, context.Id.Name, cancellationToken);
        if (resolved.TryGetValue(out var pointsAt) && pointsAt == templateId) {
            return ReconcileOutcome.InProgress($"the name '{context.Id.Name}' still resolves to this template", TimeSpan.FromSeconds(5));
        }

        if (resolved.IsFailure && resolved.Error!.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(resolved.Error);
        }

        context.Log.Report("unregistered", $"'{context.Id.Name}' no longer names this template; its versions are kept", 100);
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) {
        var tenantId = context.Id.TenantId;
        var templateId = CommunicationTemplates.TemplateIdOf(context.Id);

        var resolved = await plane.ResolveTemplateAsync(tenantId, CommunicationServices.ServiceIdOf(context.Id), context.Id.Name, cancellationToken);
        if (!resolved.TryGetValue(out var pointsAt) || pointsAt != templateId) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the name does not resolve to this template" };
        }

        var versions = await plane.ListTemplateVersionsAsync(tenantId, templateId, cancellationToken);
        if (!versions.TryGetValue(out var list) || Newest(list) is not { } newest) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the template has no version yet" };
        }

        var matches = CommunicationTemplates.Matches(newest, context.Desired);

        return new() {
            Exists = true,
            Json = new JsonObject {
                ["version"] = newest.Version,
                ["channel"] = ChannelKinds.Spell(newest.Channel),
                ["approval"] = newest.Approval.ToString(),
                ["locale"] = newest.Bodies.IsDefaultOrEmpty ? string.Empty : newest.Bodies[0].Locale,
                ["versions"] = list.Length
            }.ToJsonString(),
            ObservedAt = clock.UtcNow,
            Revision = newest.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Summary = matches ? "the newest version carries the desired body" : "the newest version has drifted from the body"
        };
    }

    static MessageTemplateVersion? Newest(ImmutableArray<MessageTemplateVersion> versions) =>
        versions.IsDefaultOrEmpty ? null : versions[^1];
}
