using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CyberCloud.Providers.Mail.ClusterConformance;

/// <summary>
///     Verifies one RFC 6376 <c>DKIM-Signature</c> — <c>rsa-sha256</c>, <c>relaxed/relaxed</c> —
///     against a public key, the way a receiving mail server does.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A VERIFIER WRITTEN FROM THE RFC, IN THE TEST, AND THAT IS THE POINT.</b> The signer is
///         Rspamd and the key is the vault's; the question <c>MailDeliveryOnK3sTests</c> asks is
///         whether a receiver that knows nothing of either — only the published record — accepts the
///         signature. So this reads the message as delivered, canonicalizes it per RFC 6376 § 3.4.2
///         and § 3.4.4, and checks the body hash and the RSA signature over the headers the
///         signature names. A verifier shared with the signer's code could agree with a signer that
///         was wrong in the same way.
///     </para>
///     <para>
///         ⚠ <b>Only what Rspamd emits.</b> <c>relaxed/relaxed</c> is what it always uses and
///         <c>rsa-sha256</c> is what a 2048-bit key signs with; any other algorithm or
///         canonicalization is refused by name rather than half-supported. No <c>l=</c>, which
///         Rspamd does not write and which a verifier should distrust anyway.
///     </para>
/// </remarks>
static class DkimVerifier {
    /// <summary>The outcome, and why.</summary>
    public readonly record struct Verdict(bool Valid, string Detail, IReadOnlyDictionary<string, string> Tags);

    /// <summary>Verifies the first <c>DKIM-Signature</c> of a message.</summary>
    /// <param name="message">The message as delivered, lines joined with CRLF.</param>
    /// <param name="publishedKey">The base64 <c>p=</c> tag of the DKIM record — a SubjectPublicKeyInfo.</param>
    public static Verdict Verify(string message, string publishedKey) {
        var split = message.IndexOf("\r\n\r\n", StringComparison.Ordinal);

        if (split < 0) {
            return new(false, "the message has no header/body separator", new Dictionary<string, string>());
        }

        var headers = Unfold(message[..(split + 2)]);
        var body = message[(split + 4)..];
        var signature = headers.FirstOrDefault(static x => x.Name.Equals("DKIM-Signature", StringComparison.OrdinalIgnoreCase));

        if (signature.Name is null) {
            return new(false, "the message carries no DKIM-Signature", new Dictionary<string, string>());
        }

        var tags = Tags(signature.Value);

        if (tags.GetValueOrDefault("a") != "rsa-sha256" || tags.GetValueOrDefault("c") != "relaxed/relaxed") {
            return new(false, $"a={tags.GetValueOrDefault("a")} c={tags.GetValueOrDefault("c")}, and this verifier reads rsa-sha256 relaxed/relaxed", tags);
        }

        // ── The body hash, § 3.7 ────────────────────────────────────────────────────────────────
        var bodyHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(RelaxedBody(body))));

        if (bodyHash != tags["bh"]) {
            return new(false, $"the body hash is {bodyHash} and the signature says {tags["bh"]}: the body changed in transit", tags);
        }

        // ── The header hash: the signed headers, bottom-up per name, then the signature itself ──
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var input = new StringBuilder();

        foreach (var name in tags["h"].Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) {
            var instances = headers.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            var skip = used.GetValueOrDefault(name);

            used[name] = skip + 1;

            // ⚠ § 5.4.2: a name listed more times than it occurs signs its ABSENCE — the oversigning
            // Rspamd does for From and Subject so nobody can add a second one. It contributes nothing.
            if (skip >= instances.Count) {
                continue;
            }

            var header = instances[instances.Count - 1 - skip];
            input.Append(RelaxedHeader(header.Name, header.Value)).Append("\r\n");
        }

        var emptied = Regex.Replace(signature.Value, @"(^|;)(\s*b\s*=)[^;]*", "$1$2", RegexOptions.None, TimeSpan.FromSeconds(1));
        input.Append(RelaxedHeader(signature.Name, emptied));

        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publishedKey), out _);

        var valid = rsa.VerifyData(
            Encoding.UTF8.GetBytes(input.ToString()),
            Convert.FromBase64String(Regex.Replace(tags["b"], @"\s", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(1))),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );

        return new(valid, valid ? "the signature verifies against the published key" : "the RSA signature does not verify against the published key", tags);
    }

    /// <summary>The headers, unfolded, in order, each with its raw value (everything after the colon).</summary>
    static List<(string Name, string Value)> Unfold(string block) {
        var headers = new List<(string Name, string Value)>();

        foreach (var line in block.Split("\r\n")) {
            if (line.Length == 0) {
                continue;
            }

            if ((line[0] == ' ' || line[0] == '\t') && headers.Count > 0) {
                var last = headers[^1];
                headers[^1] = (last.Name, last.Value + "\r\n" + line);
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            headers.Add((line[..colon], line[(colon + 1)..]));
        }

        return headers;
    }

    /// <summary>§ 3.4.2: lower-case name, unfolded, runs of whitespace to one space, trimmed.</summary>
    static string RelaxedHeader(string name, string value) {
        var unfolded = value.Replace("\r\n", string.Empty, StringComparison.Ordinal);
        var collapsed = Regex.Replace(unfolded, @"[ \t]+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();

        return name.Trim().ToLowerInvariant() + ":" + collapsed;
    }

    /// <summary>§ 3.4.4: trailing whitespace off every line, runs to one space, empty lines off the end.</summary>
    static string RelaxedBody(string body) {
        var lines = body.Split("\r\n")
            .Select(static x => Regex.Replace(x, @"[ \t]+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).TrimEnd(' '))
            .ToList();

        while (lines.Count > 0 && lines[^1].Length == 0) {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines.Count == 0 ? string.Empty : string.Join("\r\n", lines) + "\r\n";
    }

    static Dictionary<string, string> Tags(string value) {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var part in value.Replace("\r\n", string.Empty, StringComparison.Ordinal).Split(';')) {
            var equals = part.IndexOf('=', StringComparison.Ordinal);

            if (equals > 0) {
                tags[part[..equals].Trim()] = Regex.Replace(part[(equals + 1)..], @"\s", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(1));
            }
        }

        return tags;
    }
}
