using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CyberCloud.Identity.Host.Tests.Infrastructure;

/// <summary>
///     An <see cref="IHostEnvironment" /> with a chosen name, for the registrations that gate on
///     Development — the tenant fallback, the key file, the first-party defaults.
/// </summary>
/// <param name="name">
///     <see cref="Environments.Development" />, <see cref="Environments.Production" />, or anything
///     else — the gates ask <c>IsDevelopment()</c>, so anything else is the other side.
/// </param>
public sealed class TestEnvironment(string name) : IHostEnvironment {
    /// <summary>The development side of every gate.</summary>
    public static TestEnvironment Development { get; } = new(Environments.Development);

    /// <summary>The other side.</summary>
    public static TestEnvironment Production { get; } = new(Environments.Production);

    /// <inheritdoc />
    public string EnvironmentName { get; set; } = name;

    /// <inheritdoc />
    public string ApplicationName { get; set; } = "CyberCloud.Identity.Host.Tests";

    /// <inheritdoc />
    public string ContentRootPath { get; set; } = Path.GetTempPath();

    /// <inheritdoc />
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
