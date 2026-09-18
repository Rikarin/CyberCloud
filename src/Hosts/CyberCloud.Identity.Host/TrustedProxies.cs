using Microsoft.AspNetCore.Builder;
using System.Net;

namespace CyberCloud.Identity.Host;

/// <summary>
///     Turns <see cref="IdentityHostOptions.TrustedProxies" /> into the forwarded-headers
///     middleware's known-proxy list, and says whether the middleware belongs in the pipeline at all.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The middleware is in the pipeline only when the list is not empty, and the reason is
///             what <c>CyberCloud.ServiceDefaults</c> leaves behind.
///         </b> <c>AddServiceDefaults</c>
///         configures <c>ForwardedHeadersOptions</c> with both known lists cleared, and cleared lists
///         are not "trust nobody" — <c>ForwardedHeadersMiddleware</c> checks the connection's
///         address against them only when at least one has an entry, so on the cleared options it
///         believes an <c>X-Forwarded-For</c> from anywhere. Running it on those options would let
///         every caller name the address the per-IP buckets count them under. So the host runs it
///         only once a deployment has named its ingress, on lists that hold exactly that.
///     </para>
///     <para>
///         A bare address goes to <c>KnownProxies</c>, a CIDR block to <c>KnownIPNetworks</c>, and
///         anything else is a start-up failure that names the setting — the same treatment
///         <c>IdentityHostOptions.Issuer</c> gets from OpenIddict, for the same reason: a proxy list
///         that half-parsed would be a limit that half-works.
///     </para>
/// </remarks>
public static class TrustedProxies {
    /// <summary>
    ///     Whether <see cref="IdentityComposition.MapIdentityHost" /> runs the forwarded-headers
    ///     middleware for these options.
    /// </summary>
    /// <param name="options">The host's options.</param>
    /// <returns><c>true</c> if at least one proxy is named.</returns>
    public static bool AreConfigured(IdentityHostOptions options) {
        ArgumentNullException.ThrowIfNull(options);

        return options.TrustedProxies.Count > 0;
    }

    /// <summary>
    ///     Makes <paramref name="forwarded" />'s known lists exactly <paramref name="identity" />'s
    ///     list — nothing the framework or <c>AddServiceDefaults</c> put there survives.
    /// </summary>
    /// <param name="forwarded">The middleware's options.</param>
    /// <param name="identity">The host's options, for the list.</param>
    /// <exception cref="InvalidOperationException">
    ///     An entry is neither an IP address nor a CIDR block. The message quotes it and names
    ///     <c>CyberCloud:Identity:TrustedProxies</c>.
    /// </exception>
    public static void Apply(ForwardedHeadersOptions forwarded, IdentityHostOptions identity) {
        ArgumentNullException.ThrowIfNull(forwarded);
        ArgumentNullException.ThrowIfNull(identity);

        // Exactly the configured list. The framework's default is loopback, AddServiceDefaults
        // clears that, and neither is this host's decision — a list that depended on which
        // Configure ran last would be a list nobody could read off the configuration.
        forwarded.KnownProxies.Clear();
        forwarded.KnownIPNetworks.Clear();

        foreach (var entry in identity.TrustedProxies) {
            var trimmed = entry?.Trim() ?? string.Empty;

            if (IPNetwork.TryParse(trimmed, out var network)) {
                forwarded.KnownIPNetworks.Add(network);
            } else if (IPAddress.TryParse(trimmed, out var address)) {
                forwarded.KnownProxies.Add(address);
            } else {
                throw new InvalidOperationException(
                    $"{IdentityHostOptions.SectionName}:TrustedProxies holds '{entry}', which is neither an IP address "
                    + "nor a CIDR block. Name the ingress's address or its network, and nothing wider."
                );
            }
        }

        // ⚠ One hop, on purpose: the rightmost X-Forwarded-For entry is the one the ingress appended,
        // and the entries before it are whatever the caller sent. The default is already 1; it is
        // written so a change to it is a decision made here rather than in the framework.
        forwarded.ForwardLimit = 1;
    }
}
