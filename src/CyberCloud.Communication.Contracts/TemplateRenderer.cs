using System.Collections.Immutable;
using System.Text;

namespace CyberCloud.Communication.Contracts;

/// <summary>
///     Substitutes arguments into a template body, and refuses before dispatch when one is missing.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/17 § The parts that are actually the work asks for templates that are
///         <i>"named, versioned, localised, with typed parameters"</i>, and gives two reasons:
///         WhatsApp <i>requires</i> pre-approved templates, and <i>"the alternative is string
///         concatenation in twenty providers"</i>. This type is the second reason answered — one
///         substitution, in one place, that every channel shares.
///     </para>
///     <para>
///         ⚠ <b>A missing required argument fails here, and "here" is before the carrier.</b> The
///         alternative is a customer receiving <c>"Your verification code is {code}"</c>, which
///         costs a support ticket, a wasted SMS, and — because the recipient now has a message that
///         looks broken — a complaint. It is also the failure that survives review: the template
///         renders fine for every locale somebody tested.
///     </para>
///     <para>
///         <b>A pure function, deliberately.</b> It takes no grain, no clock and no service, so the
///         template tests are the fast half of the suite and the rule "no dispatch on a bad render"
///         is checkable without a silo.
///     </para>
/// </remarks>
public static class TemplateRenderer {
    /// <summary>
    ///     Renders one version in one locale, or says exactly what is missing.
    /// </summary>
    /// <param name="version">The template version. Its parameters are the contract.</param>
    /// <param name="locale">
    ///     The locale asked for. Falls back to the language alone (<c>cs-CZ</c> → <c>cs</c>), then to
    ///     <c>en</c>, then to whichever body is first. ⚠ A fallback rather than a failure because a
    ///     tenant adding a locale should not break the recipients who do not have one — but the
    ///     locale actually used comes back on <see cref="RenderedMessage.Locale" /> so a caller can
    ///     see it happened.
    /// </param>
    /// <param name="arguments">What to substitute. Extra arguments are ignored; missing required ones are not.</param>
    /// <returns>
    ///     The rendered subject and body, or <see cref="ErrorCode.InvalidRequestBody" /> naming
    ///     every missing parameter at once — a caller fixing them one round trip at a time is a
    ///     caller we made do the work.
    /// </returns>
    public static Result<RenderedMessage> Render(
        MessageTemplateVersion version,
        string? locale,
        ImmutableArray<TemplateArgument> arguments
    ) {
        ArgumentNullException.ThrowIfNull(version);

        var supplied = arguments.IsDefault ? [] : arguments;

        var missing = new List<string>();
        foreach (var parameter in version.Parameters.IsDefault ? [] : version.Parameters) {
            if (!parameter.Required) {
                continue;
            }

            if (!TryFind(supplied, parameter.Name, out _)) {
                missing.Add(parameter.Name);
            }
        }

        if (missing.Count > 0) {
            return Result<RenderedMessage>.Failure(
                ErrorCode.InvalidRequestBody,
                $"Template version {version.Version} requires "
                + $"{string.Join(", ", missing.Select(x => $"'{x}'"))} and the send supplied "
                + (supplied.Length == 0
                    ? "no arguments"
                    : $"only {string.Join(", ", supplied.Select(x => $"'{x.Name}'"))}")
                + ". Refused before dispatch — docs/plan/17 § The parts that are actually the work. "
                + "A carrier would have sent the placeholder text to the recipient."
            );
        }

        var body = Choose(version.Bodies, locale);
        if (body is null) {
            return Result<RenderedMessage>.Failure(
                ErrorCode.ResourceNotFound,
                $"Template version {version.Version} has no body in any locale, so there is nothing "
                + "to send. A version is created with at least one."
            );
        }

        return Result<RenderedMessage>.Success(
            new() {
                Subject = Substitute(body.Subject, supplied),
                Body = Substitute(body.Body, supplied),
                Locale = body.Locale,
                Version = version.Version,
                ProviderTemplateName = version.ProviderTemplateName
            }
        );
    }

    /// <summary>The body for a locale, by the fallback chain on <see cref="Render" />.</summary>
    static LocalizedBody? Choose(ImmutableArray<LocalizedBody> bodies, string? locale) {
        if (bodies.IsDefaultOrEmpty) {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(locale)) {
            foreach (var candidate in bodies) {
                if (string.Equals(candidate.Locale, locale, StringComparison.OrdinalIgnoreCase)) {
                    return candidate;
                }
            }

            var dash = locale.IndexOf('-', StringComparison.Ordinal);
            var language = dash > 0 ? locale[..dash] : locale;

            foreach (var candidate in bodies) {
                if (string.Equals(candidate.Locale, language, StringComparison.OrdinalIgnoreCase)
                    || candidate.Locale.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase)) {
                    return candidate;
                }
            }
        }

        foreach (var candidate in bodies) {
            if (candidate.Locale.StartsWith("en", StringComparison.OrdinalIgnoreCase)) {
                return candidate;
            }
        }

        return bodies[0];
    }

    /// <summary>
    ///     Replaces every <c>{name}</c> with its argument.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Left to right, single pass, and a substituted value is never re-scanned.</b> A value
    ///     containing <c>{other}</c> is text, not a placeholder. Re-scanning would let a caller who
    ///     controls one argument reach a parameter they were not given — the template-injection
    ///     shape, and on a channel that sends password-reset links it is worth a paragraph.
    ///     <para>
    ///         An unmatched <c>{name}</c> is left exactly as written rather than blanked. It can only
    ///         be an optional parameter or a typo in the template, and both are things a tenant
    ///         needs to see in the message they are testing.
    ///     </para>
    /// </remarks>
    static string Substitute(string text, ImmutableArray<TemplateArgument> arguments) {
        if (text.Length == 0 || text.IndexOf('{', StringComparison.Ordinal) < 0) {
            return text;
        }

        var built = new StringBuilder(text.Length + 32);
        var i = 0;

        while (i < text.Length) {
            var open = text.IndexOf('{', i);
            if (open < 0) {
                built.Append(text, i, text.Length - i);
                break;
            }

            var close = text.IndexOf('}', open + 1);
            if (close < 0) {
                built.Append(text, i, text.Length - i);
                break;
            }

            built.Append(text, i, open - i);

            var name = text[(open + 1)..close];
            built.Append(TryFind(arguments, name, out var value) ? value : text[open..(close + 1)]);

            i = close + 1;
        }

        return built.ToString();
    }

    /// <summary>
    ///     ⚠ Ordinal, case-sensitive. A parameter named <c>code</c> and an argument named
    ///     <c>Code</c> are two names, and matching them would make the template's declared contract
    ///     softer than the compiler's — which is the opposite of what "typed parameters" bought.
    /// </summary>
    static bool TryFind(ImmutableArray<TemplateArgument> arguments, string name, out string value) {
        foreach (var argument in arguments) {
            if (string.Equals(argument.Name, name, StringComparison.Ordinal)) {
                value = argument.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
