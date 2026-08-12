using CyberCloud.Identity;
using CyberCloud.Identity.Contracts;
using Microsoft.Extensions.Configuration;

namespace CyberCloud.Silo.Host;

/// <summary>
///     Where this silo sends the platform's own one-time codes from. docs/plan/11 § Credentials,
///     docs/plan/17.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two ids, and neither of them is the user's tenant.</b>
///         <c>OtpDeliveryRoute.TenantId</c> names the tenant that owns the
///         <c>CyberCloud.Communication/services/{name}</c> resource the codes go out through, which
///         is a platform-level fact: a sign-in code is the platform notifying a person, not a tenant
///         notifying their customer. <c>CommunicationOtpDelivery</c>'s remarks carry the three
///         consequences of routing every tenant's codes through one service, including the one that
///         is a cost rather than a benefit — the spend cap is shared.
///     </para>
///     <para>
///         ⚠ <b>They are configuration on the <i>silo</i> because the silo is what calls the seam.</b>
///         <c>UserGrain.IssueOtpAsync</c> is the only caller of <c>IOtpDeliverySeam</c>, and
///         <see cref="OtpPolicy" /> is where the argument lives for why issuing a code is a grain's
///         job rather than the identity host's. The identity host never holds a code, so it needs
///         neither of these values.
///     </para>
/// </remarks>
public sealed class SiloIdentityOptions {
    /// <summary>The configuration section this binds from.</summary>
    public const string SectionName = "CyberCloud:Identity:OtpDelivery";

    /// <summary>The tenant that owns the communication service. ⚠ Not the user's tenant.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The communication service resource the platform's codes are sent through.</summary>
    public Guid ServiceId { get; set; }

    /// <summary>
    ///     A template within that service, or empty for a free-text body.
    /// </summary>
    /// <remarks>
    ///     ⚠ WhatsApp requires one and <c>IMessageGrain</c> is what refuses a free-text send on that
    ///     channel — not this type, and not <c>AddCommunicationOtpDelivery</c>. Duplicating that rule
    ///     here would be a second copy of it that can drift.
    /// </remarks>
    public string TemplateName { get; set; } = string.Empty;

    /// <summary>
    ///     Whether both ids are set. ⚠ An unset pair is a silo that has deliberately not opted in.
    /// </summary>
    public bool IsConfigured => TenantId != Guid.Empty && ServiceId != Guid.Empty;
}

/// <summary>Composes the identity module onto this silo.</summary>
public static class SiloIdentityComposition {
    /// <summary>
    ///     Adds the identity grains' services and, when configured, points
    ///     <c>IOtpDeliverySeam</c> at <c>CyberCloud.Communication</c>.
    /// </summary>
    /// <param name="silo">The silo being composed.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE PEPPER IS EMPTY AND THAT IS A REAL COST RATHER THAN A DEVELOPMENT
    ///         CONVENIENCE.</b> <c>AddCyberCloudIdentity</c> takes the Argon2id secret input and the
    ///         HMAC key <c>OtpCodeProtector</c> derives a stored code digest under, and docs/plan/11
    ///         § Credentials asks for both to come "from Vault". Nothing in this repository
    ///         provisions a vault (docs/plan/18), so there is nothing to read one from and the
    ///         alternative — a value in configuration — is a pepper written next to the hashes it
    ///         protects, which is not a pepper. What is lost, stated so it can be paid: a stolen
    ///         durable-tier backup is enough to start a dictionary attack on the password hashes,
    ///         and enough to grind a six-digit one-time code out of its digest inside the ten minutes
    ///         it is live. Wiring a vault is a change to this one call.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An unconfigured <c>CyberCloud:Identity:OtpDelivery</c> leaves
    ///         <c>UnavailableOtpDelivery</c> in place, which is the point rather than an oversight.</b>
    ///         That type's message names the missing call and the section, and it is the sentence an
    ///         operator meets on the first code this silo is asked to send. Defaulting to a route
    ///         nobody chose would send a tenant's authentication traffic through whatever service id
    ///         happened to be first.
    ///     </para>
    /// </remarks>
    public static ISiloBuilder AddSiloIdentity(this ISiloBuilder silo) {
        ArgumentNullException.ThrowIfNull(silo);

        silo.AddCyberCloudIdentity();

        var options = new SiloIdentityOptions();
        silo.Configuration.GetSection(SiloIdentityOptions.SectionName).Bind(options);

        if (options.IsConfigured) {
            silo.AddCommunicationOtpDelivery(options.TenantId, options.ServiceId, options.TemplateName);
        }

        return silo;
    }
}
