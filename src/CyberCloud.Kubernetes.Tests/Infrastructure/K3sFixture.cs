using System.Text;
using CyberCloud.Kubernetes.Apply;
using k8s;
using k8s.Models;
using Testcontainers.K3s;

namespace CyberCloud.Kubernetes.Tests.Infrastructure;

/// <summary>
///     A real k3s API server — docs/plan/09 § Testing the fabric, "Fast, real API server, real SSA
///     semantics".
/// </summary>
/// <remarks>
///     ⚠ <b>Real, and not a fake, because the property under test belongs to the API server.</b>
///     ADR-013's payoff is that a second field manager's edit produces a <i>conflict</i> rather than
///     a silent revert. That is server-side apply's behaviour, implemented in
///     <c>kube-apiserver</c>'s field-management machinery — nothing we wrote. A fake would assert our
///     belief about what the API server does, which is precisely the belief worth checking.
/// </remarks>
public sealed class K3sFixture : IAsyncLifetime
{
    /// <summary>
    ///     The k3s image, pinned.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Pinned explicitly, and not the package's default.</b> Testcontainers.K3s 4.13.0
    ///     defaults to <c>rancher/k3s:v1.26.2-k3s1</c> — Kubernetes 1.26, released in December 2022
    ///     and long out of support — and its parameterless <c>K3sBuilder()</c> is <c>[Obsolete]</c>
    ///     for exactly that reason. Server-side apply's conflict behaviour is stable across those
    ///     versions, but the fabric is written against <c>KubernetesClient</c> 19.0.2, whose models
    ///     are generated from Kubernetes 1.35; testing against 1.26 would leave a nine-release gap
    ///     between what the tests prove and what the client expects. 1.35 matches the client.
    /// </remarks>
    public const string Image = "rancher/k3s:v1.35.7-k3s1";

    readonly K3sContainer container = new K3sBuilder(Image).Build();

    /// <summary>The raw client, for the parts of a test that are deliberately not us.</summary>
    public IKubernetes Raw { get; private set; } = null!;

    /// <summary>The fabric's client — the thing under test.</summary>
    public IKubeApiClient Api { get; private set; } = null!;

    /// <summary>The namespace every test in this fixture writes into.</summary>
    public const string Namespace = "cc-fabric";

    /// <summary>The cluster id the fabric addresses this container by.</summary>
    public static readonly Guid ClusterId = Guid.Parse("3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d");

    /// <summary>The kubeconfig, for tests that build their own client.</summary>
    public string Kubeconfig { get; private set; } = string.Empty;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var token = TestContext.Current.CancellationToken;

        await container.StartAsync(token);

        Kubeconfig = await container.GetKubeconfigAsync();

        using var yaml = new MemoryStream(Encoding.UTF8.GetBytes(Kubeconfig));
        var config = await KubernetesClientConfiguration.BuildConfigFromConfigFileAsync(yaml);

        Raw = new k8s.Kubernetes(config);
        Api = new KubeApiClient(Raw, ClusterId, new TestClock(), ownsClient: false);

        await Raw.CoreV1.CreateNamespaceAsync(
            new V1Namespace { Metadata = new V1ObjectMeta { Name = Namespace } },
            cancellationToken: token);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Raw?.Dispose();
        await container.DisposeAsync();
    }

    /// <summary>
    ///     Applies a document as a <b>different</b> field manager — the tenant hand-editing a field
    ///     we own.
    /// </summary>
    /// <param name="manager">The rival manager's name.</param>
    /// <param name="plural">The resource plural.</param>
    /// <param name="name">The object name.</param>
    /// <param name="json">A minimal apply document naming only the fields the rival claims.</param>
    /// <param name="group">The API group.</param>
    /// <param name="version">The API version.</param>
    /// <remarks>
    ///     ⚠ <c>force: true</c> here on purpose. The rival is <i>taking</i> ownership of the field,
    ///     which is what a <c>kubectl apply --force-conflicts</c> or a tenant's own controller does.
    ///     Our side never passes <see langword="true" /> — <c>KubeCommand.Force</c> is pinned false
    ///     and the builder has no method to change it.
    /// </remarks>
    public async Task RivalApplyAsync(
        string manager,
        string plural,
        string name,
        string json,
        string group = "apps",
        string version = "v1")
    {
        using var response = await Raw.CustomObjects.PatchNamespacedCustomObjectWithHttpMessagesAsync(
            new V1Patch(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json),
                V1Patch.PatchType.ApplyPatch),
            group,
            version,
            Namespace,
            plural,
            name,
            fieldManager: manager,
            force: true,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>Reads a field's current value straight from the cluster, bypassing our code.</summary>
    /// <param name="plural">The resource plural.</param>
    /// <param name="name">The object name.</param>
    /// <param name="group">The API group.</param>
    /// <param name="version">The API version.</param>
    /// <param name="path">
    ///     The property names, one per segment — for example <c>"spec", "replicas"</c>.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>Segments, not a dotted string.</b> The first version of this helper split a
    ///     <c>"metadata.labels.cybercloud.io/tenant-id"</c> path on <c>'.'</c> and reported the
    ///     labels missing from every object — because a Kubernetes label key <i>contains dots</i>
    ///     (<c>cybercloud.io/tenant-id</c> has two) and there is no escaping convention that makes a
    ///     dotted path unambiguous over a map whose keys are DNS subdomains. This is the same class
    ///     of mistake as the label rules the suite is about, which is why the signature changed
    ///     rather than the splitting getting cleverer.
    /// </remarks>
    public async Task<string?> ReadFieldAsync(
        string plural,
        string name,
        string group,
        string version,
        params string[] path)
    {
        ArgumentNullException.ThrowIfNull(path);

        using var response = await Raw.CustomObjects.GetNamespacedCustomObjectWithHttpMessagesAsync(
            group, version, Namespace, plural, name,
            cancellationToken: TestContext.Current.CancellationToken);

        var element = (System.Text.Json.JsonElement)response.Body!;

        foreach (var segment in path)
        {
            if (!element.TryGetProperty(segment, out element))
            {
                return null;
            }
        }

        return element.ToString();
    }

    /// <summary>
    ///     <see cref="ReadFieldAsync(string, string, string, string, string[])" /> for the
    ///     <c>apps/v1</c> group, which most of the suite uses.
    /// </summary>
    /// <param name="plural">The resource plural.</param>
    /// <param name="name">The object name.</param>
    /// <param name="path">The property names, one per segment.</param>
    public Task<string?> ReadAppsFieldAsync(string plural, string name, params string[] path) =>
        ReadFieldAsync(plural, name, "apps", "v1", path);
}

/// <summary>Binds <see cref="K3sFixture" /> to the classes that share it.</summary>
[CollectionDefinition(Name)]
public sealed class K3sSuite : ICollectionFixture<K3sFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "k3s";
}
