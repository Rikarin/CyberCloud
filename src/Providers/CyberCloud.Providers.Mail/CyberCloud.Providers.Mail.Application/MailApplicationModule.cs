using CyberCloud.Core.Contracts;
using CyberCloud.Providers.Mail.Contracts;
using CyberCloud.Providers.Mail.Dns;
using CyberCloud.ResourceManager;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Collections.Immutable;
using System.Globalization;
using Volo.Abp.Application;
using Volo.Abp.Modularity;

// ── Rule 4's declaration — docs/plan/03 § Assembly graph rules ────────────────────────────────
//
// ⚠ TWO HOSTS, AND THE SECOND ONE IS NOT A CONCESSION. The silo runs the reconcilers this module
// registers; the gateway builds the SAME registry from the SAME providers, because RouteStage
// resolves a path against it and a gateway with its own list would route from a description of a
// platform the silo is not running.
[assembly: OwningHost("CyberCloud.Silo.Host")]
[assembly: OwningHost("CyberCloud.Gateway.Host")]

namespace CyberCloud.Providers.Mail.Application;

/// <summary>
///     The managed-mail provider's ABP module — what a host <c>[DependsOn]</c> to load it.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/03 § Providers:
///         <i>
///             "Each is an ABP module (<c>[DependsOn]</c>), each registers its
///             resource types into <c>CyberCloud.ResourceManager</c>."
///         </i> Both halves happen here — a host
///         that <c>[DependsOn]</c> this module gets the provider and its reconcilers, and there is no
///         second call it can forget.
///     </para>
///     <para>
///         ⚠ <b>THE FIRST PROVIDER MODULE THAT READS CONFIGURATION, AND WHAT IT READS IS THE
///         PLATFORM'S, NOT A TENANT'S.</b> <see cref="MailPlatformOptions" /> names the shared front
///         doors every domain's records point at and the abuse desk's suspensions;
///         <see cref="MailDnsOptions" /> names the nameservers the sending gate asks. The domain's
///         reconciler and both action handlers take them in their constructors, so a host that loaded
///         the provider without this module would fail its container's validation naming the missing
///         service rather than run a gate nobody configured. Both default to empty: an unconfigured
///         region holds every domain's sending and refuses <c>dnsRecords</c> by name.
///     </para>
/// </remarks>
[DependsOn(typeof(AbpDddApplicationModule))]
public sealed class MailApplicationModule : AbpModule {
    /// <inheritdoc />
    public override void ConfigureServices(ServiceConfigurationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        var configuration = context.Services.GetConfiguration();

        context.Services.TryAddSingleton(PlatformFrom(configuration));
        context.Services.TryAddSingleton(DnsFrom(configuration.GetSection(MailDnsOptions.Section)));
        context.Services.TryAddSingleton<IMailDnsResolver, DnsWireResolver>();
        context.Services.AddCyberCloudProvider(new MailProvider());
    }

    /// <summary>
    ///     Reads <see cref="MailPlatformOptions" /> out of <see cref="MailPlatformOptions.Section" />, by
    ///     hand, refusing a suspension entry that is not a tenant id.
    /// </summary>
    /// <param name="configuration">The host's configuration root.</param>
    /// <exception cref="InvalidOperationException">
    ///     An entry of <c>SuspendedTenants</c> is not a GUID. The message names the configuration key
    ///     and the value, so the operator who typed it finds it.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ By hand because the configuration binder does not populate an
    ///         <see cref="System.Collections.Immutable.ImmutableArray{T}" />, and a suspension list that
    ///         silently bound to empty is an abuse desk whose suspensions do nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>TWO SPELLINGS, BECAUSE THE SECOND ONE IS WHAT AN OPERATOR IN A HURRY TYPES.</b> The
    ///         array form (<c>SuspendedTenants:0</c>, <c>CyberCloud__Mail__SuspendedTenants__0</c>) is
    ///         the binder's; a single comma-separated value
    ///         (<c>CyberCloud__Mail__SuspendedTenants=a,b</c>) has no children at all, and the first
    ///         cut read children only — so that spelling suspended nobody and said nothing. Both are
    ///         read now, and anything that is not a GUID stops the host from starting rather than
    ///         suspending less than the desk asked for. The first cut had no test of this at all (the
    ///         #34 review); <c>MailPlatformConfigurationTests</c> is it.
    ///     </para>
    /// </remarks>
    public static MailPlatformOptions PlatformFrom(IConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(MailPlatformOptions.Section);

        return new() {
            InboundHost = section["InboundHost"] ?? string.Empty,
            SpfInclude = section["SpfInclude"] ?? string.Empty,
            MtaStsHost = section["MtaStsHost"] ?? string.Empty,
            TlsReportAddress = section["TlsReportAddress"] ?? string.Empty,
            SuspendedTenants = SuspendedTenantsFrom(section.GetSection("SuspendedTenants"))
        };
    }

    static ImmutableArray<Guid> SuspendedTenantsFrom(IConfigurationSection list) {
        var spelled = list.GetChildren().Select(static x => (x.Path, x.Value)).ToList();

        if (list.Value is { Length: > 0 } scalar) {
            spelled.AddRange(
                scalar.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(x => (list.Path, (string?)x))
            );
        }

        var tenants = ImmutableArray.CreateBuilder<Guid>(spelled.Count);

        foreach (var (path, value) in spelled) {
            if (!Guid.TryParse(value, CultureInfo.InvariantCulture, out var tenant)) {
                throw new InvalidOperationException(
                    $"Configuration '{path}' is '{value}', which is not a tenant id. Every entry of "
                    + $"{MailPlatformOptions.Section}:SuspendedTenants must be a tenant GUID — the host refuses to "
                    + "start rather than run an abuse desk whose suspension silently did nothing."
                );
            }

            tenants.Add(tenant);
        }

        return tenants.ToImmutable();
    }

    static MailDnsOptions DnsFrom(IConfigurationSection section) =>
        new() {
            Nameservers = [.. section.GetSection("Nameservers").GetChildren().Select(static x => x.Value ?? string.Empty)],
            Timeout = TimeSpan.TryParse(section["Timeout"], CultureInfo.InvariantCulture, out var timeout)
                ? timeout
                : TimeSpan.FromSeconds(3),
            TcpOnly = bool.TryParse(section["TcpOnly"], out var tcp) && tcp
        };
}
