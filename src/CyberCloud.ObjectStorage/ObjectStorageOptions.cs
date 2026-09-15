namespace CyberCloud.ObjectStorage;

/// <summary>
///     Where the platform's object storage is, and how to sign for it. The
///     <c>CyberCloud:ObjectStorage</c> section. docs/plan/13 § Artifact feeds, docs/plan/15
///     § Object storage.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Unconfigured means the refusing default stays</b>, the way <c>CyberCloud:Vault</c>
///         works: a host reads this section, and calls <c>AddS3ObjectStore</c> only when
///         <see cref="IsConfigured" /> is true. A host that opted in and got the address wrong fails
///         at composition rather than at the first push, because <c>AddS3ObjectStore</c> validates
///         what it can see — an absolute endpoint, a bucket, both halves of the credential.
///     </para>
///     <para>
///         ⚠ <b><see cref="SecretAccessKey" /> is a value, and this is the one place in the tree
///         that holds one outside <c>CyberCloud.Vault</c>.</b> It arrives from configuration — in a
///         deployment, a Kubernetes <c>Secret</c> projected into the environment — and it is read
///         into an HMAC key and nowhere else: never logged, never on a wire type, never in grain
///         state. CC1005 polices <c>[Id]</c> members and this class has none; what keeps the value
///         off the wire is that nothing in this assembly declares a wire type.
///     </para>
///     <para>
///         Path-style addressing (<c>{Endpoint}/{Bucket}/{key}</c>) and nothing else. SeaweedFS
///         serves path-style by default, virtual-host style needs a wildcard DNS entry the platform
///         does not provision, and one addressing style is one thing to sign.
///     </para>
/// </remarks>
public sealed class ObjectStorageOptions {
    /// <summary>The configuration section.</summary>
    public const string SectionName = "CyberCloud:ObjectStorage";

    /// <summary>The S3 endpoint's origin — <c>https://s3.platform.internal:8333</c>.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>The one bucket every platform host writes into. Keys carry the tenant.</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>
    ///     The region in the signature's credential scope. SeaweedFS ignores it; a real S3 pins it.
    /// </summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>The access key id — the public half of the credential.</summary>
    public string AccessKeyId { get; set; } = string.Empty;

    /// <summary>The secret half. See the remarks on the class.</summary>
    public string SecretAccessKey { get; set; } = string.Empty;

    /// <summary>
    ///     Whether a plain-<c>http</c> endpoint is accepted. Off, so a production section that
    ///     drops the <c>s</c> fails to compose rather than sending a signed credential in clear.
    /// </summary>
    public bool AllowInsecureTransport { get; set; }

    /// <summary>How long one request may take. Artefacts are bounded, so ten seconds is generous.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether the section names an endpoint, a bucket and a credential at all.</summary>
    public bool IsConfigured =>
        Endpoint.Length > 0 && Bucket.Length > 0 && AccessKeyId.Length > 0 && SecretAccessKey.Length > 0;
}
