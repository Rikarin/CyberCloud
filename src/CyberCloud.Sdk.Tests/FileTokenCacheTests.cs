using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CyberCloud.Sdk.Tests;

/// <summary>
///     <see cref="FileTokenCache" /> — the owner-only fallback for a machine with no keychain (#43).
/// </summary>
/// <remarks>
///     ⚠ Against a temporary directory and the real file system, on whichever operating system runs
///     the suite: the Unix mode assertions run on Unix and the DACL assertions on Windows, because
///     the two are different mechanisms and neither can be faked on the other.
/// </remarks>
public sealed class FileTokenCacheTests : IDisposable {
    readonly string directory = Path.Combine(Path.GetTempPath(), "cyc-token-cache-tests", Guid.NewGuid().ToString("N"));

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static TokenCacheRecord Record { get; } = new() {
        RefreshToken = "refresh-" + new string('r', 2_000),
        AccessToken = "access-" + new string('a', 900),
        ExpiresOn = DateTimeOffset.UnixEpoch.AddYears(60),
        Authority = "https://login.cybercloud.example/",
        ClientId = "cyc-cli"
    };

    const string Key = "login.cybercloud.example|cyc-cli|contoso";

    [Fact]
    public async Task AnEntryRoundTripsIsOwnerOnlyAndIsNamedByADigestOfItsKey() {
        var cache = new FileTokenCache(directory);

        await cache.SetAsync(Key, Record, Ct);

        (await cache.GetAsync(Key, Ct)).ShouldBe(Record);

        var path = cache.PathFor(Key);

        File.Exists(path).ShouldBeTrue();
        FileTokenCache.IsOwnerOnly(path).ShouldBeTrue();
        // ⚠ Not "contoso" in a directory listing — FileTokenCache's remarks.
        Path.GetFileName(path).ShouldNotContain("contoso");
        Directory.GetFiles(directory).ShouldBe([path], "a staging file was left behind");

        if (!OperatingSystem.IsWindows()) {
            File.GetUnixFileMode(path).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.GetUnixFileMode(directory)
                .ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        } else {
            OnlyTheCurrentUserIsAllowed(path);
        }

        await cache.RemoveAsync(Key, Ct);

        (await cache.GetAsync(Key, Ct)).ShouldBeNull();
        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public async Task AnEntrySomebodyElseCanReadIsTreatedAsAbsent() {
        var cache = new FileTokenCache(directory);

        await cache.SetAsync(Key, Record, Ct);

        var path = cache.PathFor(Key);

        // ⚠ ssh's rule for a private key: readable by others means possibly read by others, and a
        // token that may be in somebody else's hands is not one to redeem.
        if (OperatingSystem.IsWindows()) {
            AllowEveryoneToRead(path);
        } else {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        }

        FileTokenCache.IsOwnerOnly(path).ShouldBeFalse();
        (await cache.GetAsync(Key, Ct)).ShouldBeNull();

        // And the next write narrows it again rather than writing into the wide file.
        await cache.SetAsync(Key, Record, Ct);

        FileTokenCache.IsOwnerOnly(path).ShouldBeTrue();
        (await cache.GetAsync(Key, Ct)).ShouldBe(Record);
    }

    [Fact]
    public void TheDefaultDirectoryIsThePerUserStateLocation() {
        var path = FileTokenCache.DefaultDirectory;

        path.ShouldEndWith("tokens");

        if (OperatingSystem.IsWindows()) {
            path.ShouldStartWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        } else {
            path.ShouldStartWith(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
    }

    [SupportedOSPlatform("windows")]
    static void OnlyTheCurrentUserIsAllowed(string path) {
        var security = new FileInfo(path).GetAccessControl();

        security.AreAccessRulesProtected.ShouldBeTrue("the profile's inherited ACL flowed into the token file");

        var allowed = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Where(static x => x.AccessControlType == AccessControlType.Allow)
            .Select(static x => x.IdentityReference)
            .Distinct()
            .ToList();

        allowed.ShouldBe([WindowsIdentity.GetCurrent().User!]);
    }

    [SupportedOSPlatform("windows")]
    static void AllowEveryoneToRead(string path) {
        var file = new FileInfo(path);
        var security = file.GetAccessControl();

        security.AddAccessRule(
            new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Read,
                AccessControlType.Allow
            )
        );
        file.SetAccessControl(security);
    }

    /// <inheritdoc />
    public void Dispose() {
        try {
            Directory.Delete(directory, true);
        } catch (IOException) {
            // Under the temp path; a handle still open on Windows is the only way here.
        }
    }
}
