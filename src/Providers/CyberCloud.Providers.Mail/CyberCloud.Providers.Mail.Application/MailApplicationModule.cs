using CyberCloud.Core.Contracts;
using CyberCloud.Providers.Mail.Contracts;
using CyberCloud.Providers.Mail.Dns;
using CyberCloud.ResourceManager;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        context.Services.TryAddSingleton(PlatformFrom(configuration.GetSection(MailPlatformOptions.Section)));
        context.Services.TryAddSingleton(DnsFrom(configuration.GetSection(MailDnsOptions.Section)));
        context.Services.TryAddSingleton<IMailDnsResolver, DnsWireResolver>();
        context.Services.AddCyberCloudProvider(new MailProvider());
    }

    /// <summary>Reads <see cref="MailPlatformOptions" /> by hand.</summary>
    /// <remarks>
    ///     ⚠ By hand because the configuration binder does not populate an
    ///     <see cref="System.Collections.Immutable.ImmutableArray{T}" />, and a suspension list that
    ///     silently bound to empty is an abuse desk whose suspensions do nothing.
    /// </remarks>
    static MailPlatformOptions PlatformFrom(IConfigurationSection section) =>
        new() {
            InboundHost = section["InboundHost"] ?? string.Empty,
            SpfInclude = section["SpfInclude"] ?? string.Empty,
            MtaStsHost = section["MtaStsHost"] ?? string.Empty,
            TlsReportAddress = section["TlsReportAddress"] ?? string.Empty,
            SuspendedTenants = [
                .. section.GetSection("SuspendedTenants")
                    .GetChildren()
                    .Select(static x => Guid.Parse(x.Value ?? string.Empty, CultureInfo.InvariantCulture))
            ]
        };

    static MailDnsOptions DnsFrom(IConfigurationSection section) =>
        new() {
            Nameservers = [.. section.GetSection("Nameservers").GetChildren().Select(static x => x.Value ?? string.Empty)],
            Timeout = TimeSpan.TryParse(section["Timeout"], CultureInfo.InvariantCulture, out var timeout)
                ? timeout
                : TimeSpan.FromSeconds(3),
            TcpOnly = bool.TryParse(section["TcpOnly"], out var tcp) && tcp
        };
}
