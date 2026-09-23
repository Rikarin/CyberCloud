// ⚠ For `Result<string>`, which an action answers. The `ErrorCode` alias in GlobalUsings still wins.

using CyberCloud.Core;
using CyberCloud.Providers.Mail.Dns;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Mail;

/// <summary>
///     <c>POST …/domains/{name}/dnsRecords</c> — the records the tenant must publish, derived from
///     the domain and the key the vault holds. Asks nothing of the DNS.
/// </summary>
/// <remarks>
///     ⚠ <b>Read permission, because nothing moves.</b> doc 17's <i>"with the exact records to
///     add"</i>, as a zone file and as seven typed members. A region whose mail hosts are not
///     configured is refused naming the section, rather than answered with records that point at
///     nothing — a tenant would publish them.
/// </remarks>
/// <param name="platform">The platform's mail hosts.</param>
public sealed class MailDnsRecordsHandler(MailPlatformOptions platform) : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => MailDomains.Type;

    /// <inheritdoc />
    public string Action => MailDomains.DnsRecordsAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(ActionContext context, CancellationToken cancellationToken = default) {
        var required = await RequiredAsync(context, platform, cancellationToken);

        if (required.TryGetError(out var error)) {
            return Result<string>.Failure(error);
        }

        var domain = MailDomains.Domain(context.Desired);

        return Result<string>.Success(MailDnsRecords.RecordsJson(domain, required.GetValueOrThrow().Records, platform));
    }

    /// <summary>The records for a domain, or the refusal that explains why there are none.</summary>
    internal static async Task<Result<(ImmutableArray<MailDnsRecord> Records, string Key)>> RequiredAsync(
        ActionContext context,
        MailPlatformOptions platform,
        CancellationToken cancellationToken
    ) {
        if (!platform.IsConfigured) {
            return Result<(ImmutableArray<MailDnsRecord>, string)>.Failure(
                ErrorCode.InternalError,
                "This region's shared mail hosts are not configured (" + MailPlatformOptions.Section + "), so there "
                + "is no MX, SPF include or MTA-STS host to publish. The records are refused rather than "
                + "invented, because a tenant would publish them."
            );
        }

        var key = await context.Secrets.ResolveAsync(
            MailDomains.CredentialRef(context.Id, MailDomains.DkimPrivateKeyField),
            cancellationToken
        );

        if (key.TryGetError(out var keyError)) {
            // ⚠ The domain's first pass mints the key. Before it has run there is no DKIM record to
            // give, and the vault's own refusal says which.
            return Result<(ImmutableArray<MailDnsRecord>, string)>.Failure(keyError);
        }

        var pem = key.GetValueOrThrow();

        return MailDnsRecords.TryRequired(MailDomains.Domain(context.Desired), pem, platform, out var records)
            ? Result<(ImmutableArray<MailDnsRecord>, string)>.Success((records, pem))
            : Result<(ImmutableArray<MailDnsRecord>, string)>.Failure(
                ErrorCode.InternalError,
                "The DKIM key in the vault for this domain does not parse as an RSA key, so no DKIM record "
                + "can be derived. It was minted once and is never regenerated — see MailDomains.GenerateCredentials."
            );
    }
}

/// <summary>
///     <c>POST …/domains/{name}/verify</c> — resolves every required record, reports each, and moves
///     the sending gate to match.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>WRITE PERMISSION, BECAUSE IT CAN OPEN THE GATE.</b> When the decision differs from
///         what the running <c>ConfigMap</c> carries, this re-applies it — the same document, under
///         the same identity, through the same builder chain the reconciler uses
///         (<c>MailDomainReconciler.ApplyAsync</c>) — so a tenant who published their records sends
///         within the kubelet's sync period rather than at the next write to the domain. It is how a
///         converged domain, which nothing reconciles again by itself, learns the DNS changed.
///     </para>
///     <para>
///         ⚠ The decision is <see cref="MailDeliverability.DecideAsync" />, which the reconciler also
///         calls, so the two cannot disagree about one zone.
///     </para>
/// </remarks>
/// <param name="platform">The platform's mail hosts and suspensions.</param>
/// <param name="dns">Where to resolve.</param>
public sealed class MailVerifyHandler(MailPlatformOptions platform, IMailDnsResolver dns) : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => MailDomains.Type;

    /// <inheritdoc />
    public string Action => MailDomains.VerifyAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(ActionContext context, CancellationToken cancellationToken = default) {
        var required = await MailDnsRecordsHandler.RequiredAsync(context, platform, cancellationToken);

        if (required.TryGetError(out var error)) {
            return Result<string>.Failure(error);
        }

        var domain = MailDomains.Domain(context.Desired);
        var decision = await MailDeliverability.DecideAsync(
            context.Id.TenantId,
            domain,
            required.GetValueOrThrow().Key,
            platform,
            dns,
            cancellationToken
        );

        if (context.Cluster is { } cluster) {
            var moved = await MoveGateAsync(context, cluster, decision.Sending, cancellationToken);

            if (moved.TryGetError(out var moveError)) {
                return Result<string>.Failure(moveError);
            }
        }

        var checks = decision.Checks.IsDefaultOrEmpty
            ? [.. required.GetValueOrThrow().Records.Select(static x => new MailDnsCheck(x, MailDnsRecords.Unresolvable, [], "not resolved"))]
            : decision.Checks;

        return Result<string>.Success(MailDnsRecords.VerificationJson(domain, checks, platform, decision.Sending));
    }

    /// <summary>Re-applies the domain's <c>ConfigMap</c> when the gate it carries is not the one decided.</summary>
    /// <remarks>
    ///     ⚠ Only when it differs: an apply that changed nothing would still bump nothing, but reading
    ///     first keeps a <c>verify</c> on a converged domain from being a write at all.
    /// </remarks>
    static async Task<Result> MoveGateAsync(
        ActionContext context,
        IKubeClusterConnection cluster,
        MailSending sending,
        CancellationToken cancellationToken
    ) {
        var target = MailDomains.ConfigMapRef(context.Namespace, context.Id.Name);
        var live = await cluster.GetAsync(target, cancellationToken);

        if (live.TryGetError(out var readError)) {
            // A domain whose back end is not there yet has no gate to move; its first pass will
            // render the decision itself.
            return readError.Code == ErrorCode.ResourceNotFound ? Result.Success : Result.Failure(readError);
        }

        var wanted = "smtpd_relay_restrictions = " + MailDomains.RelayRestrictions(MailDomains.Domain(context.Desired), sending) + "\n";

        if (RunningMainCf(live.GetValueOrThrow().Json).Contains(wanted, StringComparison.Ordinal)) {
            return Result.Success;
        }

        var applied = await MailDomainReconciler.ApplyAsync(
            cluster,
            context.Id,
            context.Namespace,
            context.ApiVersion,
            target,
            MailDomains.ConfigMapJson(context.Id.Name, context.Desired, sending),
            cancellationToken
        );

        return applied.TryGetError(out var applyError) ? Result.Failure(applyError) : Result.Success;
    }

    static string RunningMainCf(string configMapJson) {
        try {
            return JsonNode.Parse(configMapJson)?["data"]?[MailDomains.PostfixMainCfKey]?.GetValue<string>() ?? string.Empty;
        } catch (JsonException) {
            return string.Empty;
        }
    }
}
