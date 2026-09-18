using CyberCloud.Identity;
using CyberCloud.Identity.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

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

    /// <summary>
    ///     The tenant that owns the communication service. ⚠ Not the user's tenant — and unset is the
    ///     <b>platform tenant</b>, whose id is all zeroes, so a route to the platform's own service
    ///     (<see cref="PlatformCommunicationService" />) names only a <see cref="ServiceId" />.
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>The communication service resource the platform's codes are sent through.</summary>
    /// <remarks>
    ///     ⚠ <b>Not the resource's GUID as a listing shows it.</b> Since the tenant-facing provider
    ///     landed (#33), a <c>CyberCloud.Communication/services/{name}</c> resource's grain is keyed
    ///     by an id derived from the resource's <i>address</i> —
    ///     <c>CommunicationGrainKeys.ResourceIdFor(tenantId, canonicalPath)</c>, which
    ///     <c>CommunicationServices.ServiceIdOf</c> computes — so this is that derived id for whatever
    ///     <c>services</c> resource the platform tenant created for itself. The derivation is a pure
    ///     function of the tenant and the path, so it can be computed once and written here.
    ///     <b>Where to read it:</b> the service's own PUT operation. Its <c>ready</c> progress line
    ///     names the id (<c>CommunicationServiceReconciler</c> reports it, and progress reaches the
    ///     portal and <c>cyc --wait</c> — docs/plan/08 § The reconcile loop), so the number is read
    ///     off the operation that created the service rather than recomputed by hand. Or, since #93,
    ///     the platform's own service: <c>PlatformBootstrapTask</c> writes it whenever a relay is
    ///     configured and logs its id at start, and <see cref="PlatformCommunicationService.ServiceId" />
    ///     is the same number.
    /// </remarks>
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
    ///     Whether a service is named. ⚠ An unset section is a silo that has deliberately not opted in.
    /// </summary>
    /// <remarks>
    ///     The service id alone decides, since #93: <see cref="TenantId" /> may legitimately be all
    ///     zeroes, because that is the platform tenant, and the platform's own service is the route
    ///     most deployments want. Before, the pair had to be set and a route to the platform tenant
    ///     could not be written at all.
    /// </remarks>
    public bool IsConfigured => ServiceId != Guid.Empty;
}

/// <summary>Composes the identity module onto this silo.</summary>
public static class SiloIdentityComposition {
    /// <summary>
    ///     Adds the identity grains' services and, when configured, points
    ///     <c>IOtpDeliverySeam</c> at <c>CyberCloud.Communication</c>.
    /// </summary>
    /// <param name="silo">The silo being composed.</param>
    /// <param name="environment">The host's environment — the input the Development branch reads first.</param>
    /// <param name="hasRelay">
    ///     Whether <c>CyberCloud:Communication:Smtp</c> names a relay, so the platform's own service
    ///     can carry a code. Read only by the Development branch; a configured route ignores it.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             THE PEPPER IS EMPTY AND THAT IS A REAL COST RATHER THAN A DEVELOPMENT
    ///             CONVENIENCE.
    ///         </b> <c>AddCyberCloudIdentity</c> takes the Argon2id secret input and the
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
    ///         ⚠
    ///         <b>
    ///             An unconfigured <c>CyberCloud:Identity:OtpDelivery</c> leaves
    ///             <c>UnavailableOtpDelivery</c> in place outside Development, which is the point
    ///             rather than an oversight.
    ///         </b>
    ///         That type's message names the missing call and the section, and it is the sentence an
    ///         operator meets on the first code this silo is asked to send. Defaulting to a route
    ///         nobody chose would send a tenant's authentication traffic through whatever service id
    ///         happened to be first.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             In Development, and only there, an unconfigured route logs the code instead —
    ///             <c>DevelopmentOtpDelivery</c> — and, when the silo has a relay, mails it as well.
    ///         </b> A sign-up whose enrolment code goes nowhere is a sign-up nobody can finish.
    ///         Keyed on <paramref name="environment" /> rather than on a setting, for the reason
    ///         that type gives; a configured route still wins, so a developer who wires a real
    ///         communication service gets real delivery. The person reads the code off the silo's
    ///         console in the Aspire dashboard — the silo, because <c>OtpPolicy</c>'s fourth property
    ///         keeps the plaintext in the process that ran the grain — and, on the AppHost since
    ///         #93, in Mailpit's inbox at <c>http://localhost:8025</c>: with
    ///         <paramref name="hasRelay" /> the development seam also sends through the platform's
    ///         own communication service (<see cref="PlatformCommunicationService" />), which
    ///         <c>PlatformBootstrapTask</c> writes at start on the same condition.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The relay alone opts nobody in outside Development.</b> A Staging silo with a
    ///         relay and no route keeps <c>UnavailableOtpDelivery</c>; routing a tenant's
    ///         authentication traffic through a service the operator did not name is the default
    ///         the paragraph above refuses, and a relay is not a route.
    ///         <c>OtpSeamWiringTests.AProductionSiloWithARelayAndNoRouteStillKeepsTheRefusingSeam</c>
    ///         pins it.
    ///     </para>
    /// </remarks>
    public static ISiloBuilder AddSiloIdentity(this ISiloBuilder silo, IHostEnvironment environment, bool hasRelay = false) {
        ArgumentNullException.ThrowIfNull(silo);
        ArgumentNullException.ThrowIfNull(environment);

        silo.AddCyberCloudIdentity();

        var options = new SiloIdentityOptions();
        silo.Configuration.GetSection(SiloIdentityOptions.SectionName).Bind(options);

        if (options.IsConfigured) {
            silo.AddCommunicationOtpDelivery(options.TenantId, options.ServiceId, options.TemplateName);
        } else {
            silo.AddDevelopmentOtpDelivery(environment, hasRelay ? PlatformCommunicationService.OtpRoute : null);
        }

        return silo;
    }
}
