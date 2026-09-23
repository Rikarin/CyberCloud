using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Core.Resources;
using CyberCloud.Providers.Mail.Conformance;
using CyberCloud.Providers.Mail.Contracts;
using CyberCloud.Providers.Mail.Dns;
using CyberCloud.ResourceManager.Contracts;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using k8s;
using k8s.Models;
using Shouldly;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CyberCloud.Providers.Mail.ClusterConformance;

/// <summary>
///     The case the delivery story runs under: the domain's own case, with the DNS a real CoreDNS the
///     story publishes into.
/// </summary>
/// <remarks>
///     ⚠ A second case source over the SAME <see cref="ProviderConformanceCase" />, because the harness
///     reads its silo's wiring off the source type and the story needs a resolver the Docker-free
///     world does not have. The case — the provider, the schema, the objects — is
///     <see cref="MailDomainCase" />'s, unchanged.
/// </remarks>
public sealed class MailDeliveryCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase => MailDomainCase.ProviderCase;

    /// <summary>Where the silo's resolver asks — the story's CoreDNS, set before the harness starts.</summary>
    public static MailDnsOptions Dns { get; set; } = new();

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ And the mailbox reconciler. The harness registers the case's own reconciler and its
    ///     ancestors', and the story writes a second type of the same provider under the domain —
    ///     its first run failed every mailbox PUT with "is not registered in the container".
    /// </remarks>
    public static void ConfigureSilo(ISiloBuilder silo) =>
        silo.ConfigureServices(static services => {
            Register(services);
            services.TryAddSingleton<MailMailboxReconciler>();
        });

    /// <inheritdoc />
    public static void ConfigureHandlers(IServiceCollection services) => Register(services);

    static void Register(IServiceCollection services) {
        services.TryAddSingleton(MailConformanceWorld.Platform);
        services.TryAddSingleton<IMailDnsResolver>(new DnsWireResolver(Dns));
    }
}

/// <summary>
///     issue #34's story, on a real k3s through the real resource manager: a domain and three
///     mailboxes are created, the back end starts from the real images, a message is submitted with
///     <c>AUTH</c> and fetched over IMAP carrying a DKIM signature that verifies against the record
///     the platform told the tenant to publish — and outbound mail stays held until that record is
///     in the DNS and <c>verify</c> has seen it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FIRST TIME A MAIL DOMAIN HAS RUN UNDER TEST IN THIS REPOSITORY.</b> The first cut
///         of this type shipped a pod spec naming three images nothing built, and every suite passed
///         over it because the API server stores a <c>StatefulSet</c> whose pod will never start. Here
///         the pod has to start, Dovecot has to find a mailbox a second writer put into a
///         <c>Secret</c>, Postfix has to hand a password to Dovecot and a message to Rspamd, and a
///         receiver who knows nothing but the published record has to accept the signature.
///     </para>
///     <para>
///         ⚠ <b>ONE TEST, BECAUSE EACH STEP IS THE PRECONDITION OF THE NEXT</b> — the M1 story's
///         argument. Splitting it would start the cluster, the database and the images five times, on
///         a machine whose cluster-backed suites already take a slot one at a time.
///     </para>
///     <para>
///         ⚠ <b>What it cannot prove, named.</b> Outbound delivery to another domain: there is no
///         receiver in the cluster and no outbound pool, so the open gate is proven by a relay
///         <c>RCPT</c> being accepted, and the message is never sent. And TLS, which is the shared
///         front door's — <c>charts/managed/mail/conformance.yaml § owed</c>.
///     </para>
/// </remarks>
public sealed class MailDeliveryOnK3sTests : IAsyncLifetime {
    const string DomainName = "mail-e2e";
    const string Domain = "example.com";
    const string Password = "correct horse battery staple";
    const int SubmissionNodePort = 30587;
    const int ImapNodePort = 30143;

    /// <summary>How long the pod may take: three image pulls, a claim, and Dovecot's first start.</summary>
    static readonly TimeSpan PodBudget = TimeSpan.FromMinutes(8);

    /// <summary>How long a change to a mounted Secret or ConfigMap may take to reach a daemon.</summary>
    /// <remarks>
    ///     The kubelet's sync period is a minute plus jitter, and the Postfix start script polls every
    ///     five seconds after that — <c>MailDomains.PostfixStartScript</c>.
    /// </remarks>
    static readonly TimeSpan MountBudget = TimeSpan.FromMinutes(3);

    ClusterContainers? containers;
    IContainer? coredns;
    ClusterConformanceHarness<MailDeliveryCase>? harness;
    int dnsPort;
    Exception? failure;

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var token = TestContext.Current.CancellationToken;

        try {
            // ── A DNS server the story publishes into ────────────────────────────────────────
            dnsPort = FreePort();

            coredns = new ContainerBuilder("coredns/coredns:1.12.1")
                .WithCommand("-conf", "/Corefile")
                .WithResourceMapping(
                    Encoding.UTF8.GetBytes(
                        ".:53 {\n    file /zones/" + Domain + " " + Domain + " {\n        reload 1s\n    }\n    errors\n}\n"
                    ),
                    "/Corefile"
                )
                .WithResourceMapping(Encoding.UTF8.GetBytes(Zone(1, string.Empty)), "/zones/" + Domain)
                .WithPortBinding(dnsPort.ToString(CultureInfo.InvariantCulture), "53/tcp")
                .WithPortBinding(dnsPort.ToString(CultureInfo.InvariantCulture), "53/udp")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("CoreDNS-"))
                .Build();

            await coredns.StartAsync(token);

            MailDeliveryCase.Dns = new() { Nameservers = ["127.0.0.1:" + dnsPort.ToString(CultureInfo.InvariantCulture)] };

            // ── The Postfix image, built from this repository ─────────────────────────────────
            var postfix = new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(Path.Combine(CommittedDefinitions.RepositoryRoot, "deploy", "images", "mail-postfix"))
                .WithName(MailDomains.PostfixImage)
                .WithCleanUp(false)
                .Build();

            await postfix.CreateAsync(token);

            // ── A k3s with two NodePorts reachable from here ──────────────────────────────────
            containers = await ClusterInfrastructure.StartContainersAsync(
                token,
                static k3s => k3s.WithPortBinding(SubmissionNodePort, true).WithPortBinding(ImapNodePort, true)
            );

            await ImportAsync(containers, MailDomains.PostfixImage, token);

            harness = await ClusterConformanceHarness<MailDeliveryCase>.StartAsync(
                1,
                "mail-delivery",
                ClusterConformanceFixture<MailDeliveryCase>.BaseSiloPort,
                containers.Endpoints,
                token
            );
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            failure = ex;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (harness is not null) {
            await harness.DisposeAsync();
        }

        if (containers is not null) {
            await containers.DisposeAsync();
        }

        if (coredns is not null) {
            await coredns.DisposeAsync();
        }
    }

    [Fact]
    public async Task AMessageSubmittedWithAuthIsFetchedOverImapCarryingASignatureThePublishedKeyVerifies() {
        Assert.SkipWhen(
            harness is null,
            ClusterInfrastructure.SkipMessage(
                "CyberCloud.Mail/domains delivery",
                "submission, delivery, IMAP and a verifying DKIM signature on a real mail back end.",
                failure
            )
        );

        var token = TestContext.Current.CancellationToken;
        var rm = harness!;
        var domain = ClusterConformanceHarness<MailDeliveryCase>.Address(DomainName);

        // ── 1. The tenant's passwords, in the tenant's vault ───────────────────────────────────
        var vaultPath = "tenants/" + ConformanceIds.Tenant.ToString("D") + "/mail";
        await ClusterConformanceState<MailDeliveryCase>.Vault.MintAsync(
            vaultPath,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["alice"] = Password, ["bob"] = Password },
            token
        );

        // ── 2. The domain and three mailboxes, through the write path ──────────────────────────
        await CreateAsync(rm, domain.Path, MailDomains.Body(ConformanceIds.Cluster, domain: Domain, mailboxQuota: "1Gi"), token);

        await CreateAsync(rm, Mailbox(domain, "alice").Path, MailMailboxes.Body(ConformanceIds.Cluster, "alice", vaultPath + "#alice", aliases: ["info"]), token);
        await CreateAsync(rm, Mailbox(domain, "bob").Path, MailMailboxes.Body(ConformanceIds.Cluster, "bob", vaultPath + "#bob", quota: "100Mi"), token);
        await CreateAsync(rm, Mailbox(domain, "archive").Path, MailMailboxes.Body(ConformanceIds.Cluster, "archive"), token);

        var ns = ClusterConformanceHarness<MailDeliveryCase>.Namespace;

        // ── 3. A way in from outside the cluster, and a pod to reach ───────────────────────────
        await ExposeAsync(rm.Raw, ns, token);
        await WaitForPodAsync(rm.Raw, ns, token);

        var host = containers!.K3s.Hostname;
        var submission = containers.K3s.GetMappedPublicPort(SubmissionNodePort);
        var imap = containers.K3s.GetMappedPublicPort(ImapNodePort);

        // ── 4. Dovecot sees the mailboxes a second writer put in its Secret ────────────────────
        await EventuallyAsync(
            () => ImapLoginAsync(host, imap, "bob@" + Domain, Password, token),
            "bob@example.com never became able to sign in to IMAP",
            rm.Raw,
            ns,
            token
        );

        // ⚠ The password-less mailbox exists — it receives — and nobody signs in to it.
        foreach (var attempt in new[] { "!", "", Password }) {
            (await ImapLoginAsync(host, imap, "archive@" + Domain, attempt, token))
                .ShouldBeFalse($"archive@ accepted '{attempt}' although it has no password");
        }

        // ── 5. Submission with AUTH: local delivery accepted, relay held ───────────────────────
        var messageId = "<" + Guid.NewGuid().ToString("N") + "@" + Domain + ">";

        await EventuallyAsync(
            async () => {
                using var smtp = await MailWire.ConnectAsync(host, submission, token);
                await smtp.ExpectAsync("220", token);
                await smtp.CommandAsync("EHLO e2e.test", "250", token);

                try {
                    await smtp.CommandAsync("AUTH PLAIN " + MailWire.Plain("alice@" + Domain, Password), "235", token);
                } catch (InvalidOperationException) {
                    // Postfix has not reloaded with alice's file yet.
                    return false;
                }

                await smtp.CommandAsync("MAIL FROM:<alice@" + Domain + ">", "250", token);
                await smtp.CommandAsync("RCPT TO:<bob@" + Domain + ">", "250", token);

                // ⚠ THE GATE. Nothing is published, so a relay is refused at RCPT with the reason.
                var held = await Should.ThrowAsync<InvalidOperationException>(
                    () => smtp.CommandAsync("RCPT TO:<someone@elsewhere.example>", "250", token)
                );
                held.Message.ShouldStartWith("554");
                held.Message.ShouldContain("held until its SPF, DKIM and DMARC records verify");

                await smtp.CommandAsync("DATA", "354", token);
                await smtp.CommandAsync(
                    "From: Alice <alice@" + Domain + ">\r\nTo: bob@" + Domain + "\r\nSubject: issue 34\r\n"
                    + "Date: " + DateTimeOffset.UtcNow.ToString("r", CultureInfo.InvariantCulture) + "\r\n"
                    + "Message-ID: " + messageId + "\r\n\r\nSigned, delivered, fetched.\r\n.",
                    "250",
                    token
                );
                await smtp.CommandAsync("QUIT", "221", token);

                return true;
            },
            "alice@example.com could not submit",
            rm.Raw,
            ns,
            token
        );

        // ── 6. bob fetches it ──────────────────────────────────────────────────────────────────
        string? delivered = null;

        await EventuallyAsync(
            async () => {
                delivered = await FetchAsync(host, imap, "bob@" + Domain, messageId, token);
                return delivered is not null;
            },
            "the message never reached bob's INBOX",
            rm.Raw,
            ns,
            token
        );

        // ── 7. The records the platform tells the tenant to publish, published ─────────────────
        var records = await rm.Manager.ActionAsync(
            new() {
                Path = domain.Path,
                ApiVersion = MailDomains.V2026,
                Verb = WriteVerb.Post,
                Action = MailDomains.DnsRecordsAction,
                Caller = ClusterConformanceHarness<MailDeliveryCase>.Caller()
            },
            token
        );

        records.IsSuccess.ShouldBeTrue(records.Error?.Message);

        using var published = JsonDocument.Parse(records.GetValueOrThrow().ActionResponse);
        await coredns!.CopyAsync(
            Encoding.UTF8.GetBytes(Zone(2, published.RootElement.GetProperty("zoneFile").GetString()!)),
            "/zones/" + Domain,
            ct: token
        );

        // ── 8. A receiver who knows only the DNS accepts the signature ─────────────────────────
        var resolver = new DnsWireResolver(MailDeliveryCase.Dns);
        MailDnsAnswer key = default;

        await EventuallyAsync(
            async () => {
                key = await resolver.QueryAsync(MailDomains.DkimSelector + "._domainkey." + Domain, "TXT", token);
                return key.Resolved && key.Values.Length == 1;
            },
            "CoreDNS never served the published DKIM record",
            rm.Raw,
            ns,
            token
        );

        var verdict = DkimVerifier.Verify(
            delivered!,
            Regex.Match(key.Values[0], @"p=([A-Za-z0-9+/=]+)", RegexOptions.None, TimeSpan.FromSeconds(1)).Groups[1].Value
        );

        verdict.Tags.GetValueOrDefault("d").ShouldBe(Domain);
        verdict.Tags.GetValueOrDefault("s").ShouldBe(MailDomains.DkimSelector);
        verdict.Valid.ShouldBeTrue(verdict.Detail + "\n\n" + delivered);

        // ── 9. verify sees the records and opens the gate on the running Postfix ───────────────
        var verified = await rm.Manager.ActionAsync(
            new() {
                Path = domain.Path,
                ApiVersion = MailDomains.V2026,
                Verb = WriteVerb.Post,
                Action = MailDomains.VerifyAction,
                Caller = ClusterConformanceHarness<MailDeliveryCase>.Caller()
            },
            token
        );

        verified.IsSuccess.ShouldBeTrue(verified.Error?.Message);

        using var verification = JsonDocument.Parse(verified.GetValueOrThrow().ActionResponse);
        verification.RootElement.GetProperty("sendingEnabled").GetBoolean()
            .ShouldBeTrue(verified.GetValueOrThrow().ActionResponse);

        await EventuallyAsync(
            async () => {
                using var smtp = await MailWire.ConnectAsync(host, submission, token);
                await smtp.ExpectAsync("220", token);
                await smtp.CommandAsync("EHLO e2e.test", "250", token);
                await smtp.CommandAsync("AUTH PLAIN " + MailWire.Plain("alice@" + Domain, Password), "235", token);
                await smtp.CommandAsync("MAIL FROM:<alice@" + Domain + ">", "250", token);

                try {
                    await smtp.CommandAsync("RCPT TO:<someone@elsewhere.example>", "250", token);
                } catch (InvalidOperationException) {
                    return false;
                }

                // ⚠ Accepted, and never sent: RSET before DATA. There is no receiver to send to.
                await smtp.CommandAsync("RSET", "250", token);
                await smtp.CommandAsync("QUIT", "221", token);

                return true;
            },
            "the relay stayed held after verify reported the records published",
            rm.Raw,
            ns,
            token
        );

        // ── 10. An alias delivers to its mailbox ───────────────────────────────────────────────
        var aliasId = "<" + Guid.NewGuid().ToString("N") + "@" + Domain + ">";

        using (var smtp = await MailWire.ConnectAsync(host, submission, token)) {
            await smtp.ExpectAsync("220", token);
            await smtp.CommandAsync("EHLO e2e.test", "250", token);
            await smtp.CommandAsync("AUTH PLAIN " + MailWire.Plain("bob@" + Domain, Password), "235", token);
            await smtp.CommandAsync("MAIL FROM:<bob@" + Domain + ">", "250", token);
            await smtp.CommandAsync("RCPT TO:<info@" + Domain + ">", "250", token);
            await smtp.CommandAsync("DATA", "354", token);
            await smtp.CommandAsync(
                "From: bob@" + Domain + "\r\nTo: info@" + Domain + "\r\nSubject: to the alias\r\nMessage-ID: " + aliasId + "\r\n\r\nx\r\n.",
                "250",
                token
            );
        }

        await EventuallyAsync(
            async () => await FetchAsync(host, imap, "alice@" + Domain, aliasId, token) is not null,
            "mail for info@ never reached alice",
            rm.Raw,
            ns,
            token
        );
    }

    // ── The story's steps ─────────────────────────────────────────────────────────────────────

    static ResourceId Mailbox(ResourceId domain, string name) =>
        new(domain.TenantId, domain.SubscriptionId, domain.ResourceGroup, MailMailboxes.Type, name, Guid.Empty, domain.Name);

    static async Task CreateAsync(ClusterConformanceHarness<MailDeliveryCase> rm, string path, string body, CancellationToken token) {
        var accepted = await rm.Manager.WriteAsync(
            new() {
                Path = path,
                ApiVersion = MailDomains.V2026,
                Verb = WriteVerb.Put,
                Body = body,
                Caller = ClusterConformanceHarness<MailDeliveryCase>.Caller()
            },
            token
        );

        accepted.IsSuccess.ShouldBeTrue($"PUT {path} was refused: {accepted.Error?.Code} {accepted.Error?.Message}");

        var operation = rm.Operation(ConformanceIds.Tenant, accepted.GetValueOrThrow().OperationId);
        OperationStatus? last = null;

        for (var i = 0; i < 60; i++) {
            last = (await operation.DriveAsync()).GetValueOrThrow();

            if (last.IsTerminal) {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }

        last.ShouldNotBeNull();
        last!.State.ShouldBe(OperationState.Succeeded, $"PUT {path} ended {last.State}: {last.Error?.Message}");
    }

    /// <summary>A NodePort Service onto the domain's pod — the test's own object, not the provider's.</summary>
    /// <remarks>
    ///     ⚠ The provider renders <c>ClusterIP</c> at every setting and must: its front doors are the
    ///     shared pools (<c>MailDomains.ServiceJson</c>). This Service stands in for the pool's reach
    ///     into the cluster, selects on the same labels, and is labelled as nobody's resource.
    /// </remarks>
    static Task<V1Service> ExposeAsync(IKubernetes raw, string ns, CancellationToken token) =>
        raw.CoreV1.CreateNamespacedServiceAsync(
            new V1Service {
                Metadata = new() { Name = "mail-e2e-front-door", NamespaceProperty = ns },
                Spec = new() {
                    Type = "NodePort",
                    Selector = new Dictionary<string, string>(MailDomains.SelectorLabels(DomainName)),
                    Ports = [
                        new() { Name = "submission", Port = MailDomains.SubmissionPort, TargetPort = MailDomains.SubmissionPort, NodePort = SubmissionNodePort },
                        new() { Name = "imap", Port = MailDomains.ImapPort, TargetPort = MailDomains.ImapContainerPort, NodePort = ImapNodePort }
                    ]
                }
            },
            ns,
            cancellationToken: token
        );

    static async Task WaitForPodAsync(IKubernetes raw, string ns, CancellationToken token) {
        var deadline = DateTimeOffset.UtcNow + PodBudget;

        while (DateTimeOffset.UtcNow < deadline) {
            var pods = await raw.CoreV1.ListNamespacedPodAsync(ns, labelSelector: "app.kubernetes.io/instance=" + DomainName, cancellationToken: token);
            var pod = pods.Items.FirstOrDefault();

            if (pod?.Status?.ContainerStatuses is { Count: 3 } statuses && statuses.All(static x => x.Ready)) {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }

        throw new ShouldAssertException("the mail pod did not become ready within " + PodBudget + ":\n" + await DescribeAsync(raw, ns, token));
    }

    /// <summary>Polls a condition, and on timeout fails with the pod's state and every container's log.</summary>
    static async Task EventuallyAsync(
        Func<Task<bool>> condition,
        string failure,
        IKubernetes raw,
        string ns,
        CancellationToken token
    ) {
        var deadline = DateTimeOffset.UtcNow + MountBudget;
        Exception? last = null;

        while (DateTimeOffset.UtcNow < deadline) {
            try {
                if (await condition()) {
                    return;
                }
            } catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException) {
                last = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), token);
        }

        throw new ShouldAssertException(failure + " within " + MountBudget + ". Last error: " + last?.Message + "\n" + await DescribeAsync(raw, ns, token));
    }

    static async Task<string> DescribeAsync(IKubernetes raw, string ns, CancellationToken token) {
        var text = new StringBuilder();
        var pods = await raw.CoreV1.ListNamespacedPodAsync(ns, cancellationToken: token);

        foreach (var pod in pods.Items) {
            text.Append("pod ").Append(pod.Metadata.Name).Append(": ").Append(pod.Status?.Phase).Append('\n');

            foreach (var status in pod.Status?.ContainerStatuses ?? []) {
                text.Append("  ").Append(status.Name).Append(" ready=").Append(status.Ready)
                    .Append(' ').Append(status.State?.Waiting?.Reason ?? status.State?.Terminated?.Reason ?? "running").Append('\n');

                try {
                    using var log = await raw.CoreV1.ReadNamespacedPodLogAsync(pod.Metadata.Name, ns, status.Name, tailLines: 40, cancellationToken: token);
                    using var reader = new StreamReader(log);
                    text.Append(await reader.ReadToEndAsync(token)).Append('\n');
                } catch (k8s.Autorest.HttpOperationException) {
                    text.Append("  (no log)\n");
                }
            }
        }

        return text.ToString();
    }

    static async Task<bool> ImapLoginAsync(string host, int port, string user, string password, CancellationToken token) {
        using var imap = await MailWire.ConnectAsync(host, port, token);
        await imap.ExpectAsync("* OK", token);

        try {
            await imap.CommandAsync($"l1 LOGIN {user} \"{password}\"", "l1 OK", token);
            return true;
        } catch (InvalidOperationException) {
            return false;
        }
    }

    /// <summary>The message with a Message-ID in a user's INBOX, CRLF-joined, or <see langword="null" />.</summary>
    static async Task<string?> FetchAsync(string host, int port, string user, string messageId, CancellationToken token) {
        using var imap = await MailWire.ConnectAsync(host, port, token);
        await imap.ExpectAsync("* OK", token);
        await imap.CommandAsync($"a1 LOGIN {user} \"{Password}\"", "a1 OK", token);
        await imap.CommandAsync("a2 SELECT INBOX", "a2 OK", token);

        var search = await imap.CommandAsync($"a3 SEARCH HEADER Message-ID \"{messageId}\"", "a3 OK", token);
        var hit = Regex.Match(search, @"\* SEARCH (\d+)", RegexOptions.None, TimeSpan.FromSeconds(1));

        if (!hit.Success) {
            return null;
        }

        var fetched = await imap.CommandAsync($"a4 FETCH {hit.Groups[1].Value} BODY.PEEK[]", "a4 OK", token);

        // The literal runs from the line after "{N}" to the line before the closing ")".
        var lines = fetched.Split("\r\n");
        var start = Array.FindIndex(lines, static x => Regex.IsMatch(x, @"\{\d+\}$", RegexOptions.None, TimeSpan.FromSeconds(1))) + 1;
        var end = Array.FindLastIndex(lines, static x => x == ")");

        return string.Join("\r\n", lines[start..end]);
    }

    /// <summary>The zone, with a serial CoreDNS reloads on, and whatever the story has published.</summary>
    static string Zone(int serial, string records) =>
        "$ORIGIN " + Domain + ".\n"
        + "@ 60 IN SOA ns." + Domain + ". hostmaster." + Domain + ". " + serial.ToString(CultureInfo.InvariantCulture) + " 60 60 600 60\n"
        + "@ 60 IN NS ns." + Domain + ".\n"
        + "ns 60 IN A 127.0.0.1\n"
        + records;

    /// <summary>Saves an image out of the host's Docker and loads it into k3s's containerd.</summary>
    /// <remarks>
    ///     ⚠ The Postfix image exists only on this host — nothing publishes it
    ///     (<c>charts/managed/mail/conformance.yaml § owed</c>, <c>the-postfix-image-is-not-published</c>)
    ///     — so k3s cannot pull it. The pod spec's tag is not <c>latest</c>, so the kubelet's
    ///     <c>IfNotPresent</c> default uses what is imported here. Dovecot and Rspamd are pulled by
    ///     k3s itself, from Docker Hub, by the tags <c>MailDomains</c> pins.
    /// </remarks>
    static async Task ImportAsync(ClusterContainers cluster, string image, CancellationToken token) {
        using var docker = TestcontainersSettings.OS.DockerEndpointAuthConfig.GetDockerClientBuilder(Guid.NewGuid()).Build();
        await using var tar = await docker.Images.SaveImageAsync(image, token);
        using var buffer = new MemoryStream();
        await tar.CopyToAsync(buffer, token);

        await cluster.K3s.CopyAsync(buffer.ToArray(), "/tmp/image.tar", ct: token);

        // ⚠ `ctr`, the multicall symlink, and not `k3s ctr`: the rancher/k3s image answers the latter with
        // "No help topic for 'ctr'", and the symlink is what points at k3s's own containerd socket.
        var imported = await cluster.K3s.ExecAsync(["ctr", "-n", "k8s.io", "images", "import", "/tmp/image.tar"], token);
        imported.ExitCode.ShouldBe(0, imported.Stdout + imported.Stderr);
    }

    static int FreePort() {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var chosen = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return chosen;
    }
}
