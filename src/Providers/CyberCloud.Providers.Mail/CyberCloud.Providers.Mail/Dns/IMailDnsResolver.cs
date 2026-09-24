namespace CyberCloud.Providers.Mail.Dns;

/// <summary>
///     Asks the DNS for one name and one record kind — the seam <c>verify</c> and the sending gate
///     read the public DNS through.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE SEAM THE FIRST CUT OF THIS PROVIDER SAID DID NOT EXIST, AND IT WAS RIGHT.</b>
///         Nothing in the repository resolved a name as data before this: every other lookup is the
///         operating system's, for a connection. <c>CyberCloud.Network/dnsZones</c> would host zones,
///         not ask the internet about them, so it was never going to be this.
///     </para>
///     <para>
///         ⚠ <b>An interface because the answer depends on where you ask.</b> A silo resolves
///         through its node's resolver, which may be a split-horizon view that answers for a tenant's
///         domain differently than the internet does; a region that must see what a receiver sees
///         configures public nameservers in <see cref="MailDnsOptions" />. The test suite points it at
///         a CoreDNS container serving a zone file, which is a real DNS server and not a double.
///     </para>
/// </remarks>
public interface IMailDnsResolver {
    /// <summary>Resolves one name for one record kind.</summary>
    /// <param name="name">A fully-qualified name, with or without the trailing dot.</param>
    /// <param name="kind"><c>TXT</c>, <c>MX</c> or <c>CNAME</c>.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>
    ///     What the server answered — <see cref="MailDnsAnswer.Resolved" /> with no values for a name
    ///     that does not exist or has no record of the kind, and not resolved for a timeout, a
    ///     server failure or a malformed answer. Never an exception for any of those.
    /// </returns>
    Task<MailDnsAnswer> QueryAsync(string name, string kind, CancellationToken cancellationToken = default);
}
