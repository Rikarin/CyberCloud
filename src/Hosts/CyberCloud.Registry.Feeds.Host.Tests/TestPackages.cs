using System.IO.Compression;
using System.Text;

namespace CyberCloud.Registry.Feeds.Host.Tests;

/// <summary>
///     Packages built in memory in the shape the real tools produce — a <c>.nupkg</c> with a
///     nuspec at its root, an npm tarball with a <c>package/package.json</c> inside.
/// </summary>
public static class TestPackages {
    /// <summary>A <c>.nupkg</c>: a zip with one nuspec at its root and one file under <c>lib/</c>.</summary>
    /// <param name="id">The package id, in whatever case the test wants the nuspec to carry.</param>
    /// <param name="version">The version, in whatever spelling the test wants the nuspec to carry.</param>
    /// <param name="description">The description.</param>
    /// <param name="dependency">An optional dependency, rendered in a <c>net8.0</c> group.</param>
    /// <param name="payloadBytes">How large the one file under <c>lib/</c> is.</param>
    public static byte[] NuGet(string id, string version, string description = "A test package.", (string Id, string Range)? dependency = null, int payloadBytes = 16) {
        var dependencies = dependency is { } d
            ? $"""<dependencies><group targetFramework="net8.0"><dependency id="{d.Id}" version="{d.Range}" /></group></dependencies>"""
            : "";

        var nuspec =
            $"""
             <?xml version="1.0" encoding="utf-8"?>
             <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
               <metadata>
                 <id>{id}</id>
                 <version>{version}</version>
                 <authors>Cyber Cloud</authors>
                 <description>{description}</description>
                 <tags>test feeds</tags>
                 {dependencies}
               </metadata>
             </package>
             """;

        using var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true)) {
            Write(zip, id + ".nuspec", Encoding.UTF8.GetBytes(nuspec));
            // ⚠ Random bytes, because a zip of zeros is a few hundred bytes whatever payloadBytes says,
            // and the cap is on the bytes pushed.
            var payload = new byte[payloadBytes];
            System.Security.Cryptography.RandomNumberGenerator.Fill(payload);
            Write(zip, "lib/net8.0/" + id + ".dll", payload);
            Write(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />"u8.ToArray());
        }

        return buffer.ToArray();
    }

    /// <summary>A zip that is not a package: no nuspec at all.</summary>
    public static byte[] ZipWithoutNuspec() {
        using var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true)) {
            Write(zip, "readme.txt", "not a package"u8.ToArray());
        }

        return buffer.ToArray();
    }

    /// <summary>The multipart body <c>dotnet nuget push</c> sends: one file part named <c>package</c>.</summary>
    public static MultipartFormDataContent NuGetPush(byte[] nupkg) {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(nupkg);
        file.Headers.ContentType = new("application/octet-stream");
        content.Add(file, "package", "package.nupkg");
        return content;
    }

    /// <summary>
    ///     An npm tarball: gzip over a tar with one <c>package/package.json</c> entry, and the
    ///     publish document that carries it the way <c>npm publish</c> does.
    /// </summary>
    /// <param name="name">The package name, scoped or not.</param>
    /// <param name="version">The version.</param>
    /// <param name="description">The description.</param>
    /// <param name="tags">Extra dist-tags to send beside <c>latest</c>.</param>
    public static (string Document, byte[] Tarball, string FileName) Npm(string name, string version, string description = "A test module.", params (string Tag, string Version)[] tags) {
        var manifest = $$"""{"name":"{{name}}","version":"{{version}}","description":"{{description}}","main":"index.js"}""";
        var tarball = Gzip(Tar("package/package.json", Encoding.UTF8.GetBytes(manifest)));

        var bare = name.Contains('/', StringComparison.Ordinal) ? name[(name.IndexOf('/', StringComparison.Ordinal) + 1)..] : name;
        var fileName = $"{bare}-{version}.tgz";

        var distTags = new StringBuilder($"\"latest\":\"{version}\"");

        foreach (var (tag, target) in tags) {
            distTags.Append($",\"{tag}\":\"{target}\"");
        }

        var document =
            $$"""
              {
                "_id": "{{name}}",
                "name": "{{name}}",
                "description": "{{description}}",
                "dist-tags": { {{distTags}} },
                "versions": { "{{version}}": {{manifest}} },
                "_attachments": { "{{fileName}}": { "content_type": "application/octet-stream", "data": "{{Convert.ToBase64String(tarball)}}", "length": {{tarball.Length}} } }
              }
              """;

        return (document, tarball, fileName);
    }

    static void Write(ZipArchive zip, string name, byte[] bytes) {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    static byte[] Gzip(byte[] bytes) {
        using var buffer = new MemoryStream();

        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true)) {
            gzip.Write(bytes);
        }

        return buffer.ToArray();
    }

    /// <summary>A ustar archive with one entry — enough of the format for npm to read it.</summary>
    static byte[] Tar(string name, byte[] content) {
        var header = new byte[512];
        Encoding.ASCII.GetBytes(name).CopyTo(header, 0);
        Encoding.ASCII.GetBytes("0000644\0").CopyTo(header, 100);
        Encoding.ASCII.GetBytes("0000000\0").CopyTo(header, 108);
        Encoding.ASCII.GetBytes("0000000\0").CopyTo(header, 116);
        Encoding.ASCII.GetBytes(Convert.ToString(content.Length, 8).PadLeft(11, '0') + "\0").CopyTo(header, 124);
        Encoding.ASCII.GetBytes("00000000000\0").CopyTo(header, 136);
        Encoding.ASCII.GetBytes("        ").CopyTo(header, 148);
        header[156] = (byte)'0';
        Encoding.ASCII.GetBytes("ustar\0").CopyTo(header, 257);
        Encoding.ASCII.GetBytes("00").CopyTo(header, 263);

        var checksum = header.Sum(b => (int)b);
        Encoding.ASCII.GetBytes(Convert.ToString(checksum, 8).PadLeft(6, '0') + "\0 ").CopyTo(header, 148);

        using var buffer = new MemoryStream();
        buffer.Write(header);
        buffer.Write(content);
        buffer.Write(new byte[(512 - content.Length % 512) % 512]);
        buffer.Write(new byte[1024]);
        return buffer.ToArray();
    }
}
