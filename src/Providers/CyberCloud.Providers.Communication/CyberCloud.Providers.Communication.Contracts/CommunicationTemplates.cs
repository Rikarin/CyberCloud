using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Communication.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Communication/services/templates</c>: a named,
///     versioned body with typed variables, and the <c>render</c> action that shows what a send
///     would say.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/17 § The parts that are actually the work:
///         <i>
///             "Named, versioned, localised,
///             with typed parameters."
///         </i> Three of the four are here as written. <b>Named</b>: the
///         resource's name is what a send's <c>template</c> references, and the service is the naming
///         authority. <b>Versioned</b>: every PUT that changes the body appends a version to
///         <c>IMessageTemplateGrain</c>, which never edits one in place, so a carrier-approved body
///         stays approved. <b>Typed parameters</b>: <c>variables</c> are the names the body must be
///         given and <c>optionalVariables</c> the ones it may be, and a send missing a required one
///         is refused before dispatch.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Localised is the one this api-version cannot carry as docs/plan/17 means it, and
///             the reason is the schema model rather than the module.
///         </b> A template version holds a body
///         <i>per locale</i> — <c>LocalizedBody</c>, an array of records — and this platform's
///         schema has no array of objects (see the remarks on <c>SchemaKind.Array</c>). So a template
///         resource is one locale: <c>locale</c>, <c>subject</c>, <c>body</c>. A second language is a
///         second resource under a second name, and the send picks by name. The grain's per-locale
///         fallback chain still runs, over the one body each resource gives it, and the multi-locale
///         shape is owed to the api-version that grows the tree.
///     </para>
///     <para>
///         ⚠ <b>The template's grain is keyed by its address</b> —
///         <see cref="TemplateIdOf" /> — so a template deleted and recreated under the same name
///         lands on the same grain and its version history continues rather than restarting at 1.
///         That is the compliance property <c>IMessageTemplateGrain</c>'s remarks ask for: the body a
///         carrier approved is evidence, and evidence should not be lost to a rename-and-back.
///     </para>
/// </remarks>
public static class CommunicationTemplates {
    /// <summary>The type path, under <see cref="CommunicationServices.TypePath" />.</summary>
    public const string TypePath = CommunicationServices.TypePath + "/templates";

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(CommunicationServices.ProviderNamespace, TypePath);

    /// <summary>A variable name: what a <c>{placeholder}</c> in the body is spelled as.</summary>
    public const string VariablePattern = "[A-Za-z_][A-Za-z0-9_]*";

    /// <summary>The locale a body that names none is written in.</summary>
    public const string DefaultLocale = "en";

    /// <summary>A BCP 47 tag, required here — a body without a locale cannot be chosen for anyone.</summary>
    public const string LocalePattern = "[a-z]{2,3}(-[A-Za-z0-9]{2,8})*";

    /// <summary>The longest subject line. RFC 5322 § 2.1.1's line limit, including the header name.</summary>
    public const int SubjectMaxLength = 998;

    /// <summary>The body shape at <see cref="CommunicationServices.V2026" />.</summary>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    true,
                    Description: "The region the template is billed in."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The template's own settings."),
                new(
                    "/properties/channel",
                    SchemaKind.Text,
                    true,
                    Description: "Which channel the body is written for. A WhatsApp body is not an email body, "
                    + "and the channel decides whether carrier pre-approval is consulted at all."
                ) { AllowedValues = ChannelKinds.AllowedValues, Immutable = true, ExampleJson = "\"email\"" },
                new(
                    "/properties/locale",
                    SchemaKind.Text,
                    Description: "The BCP 47 tag the body is written in. One locale per template; a second "
                    + "language is a second template."
                ) { Pattern = LocalePattern, MaxLength = 35, DefaultJson = "\"" + DefaultLocale + "\"" },
                new(
                    "/properties/subject",
                    SchemaKind.Text,
                    Description: "The subject line, for channels that have one. Placeholders are substituted "
                    + "here too."
                ) { MaxLength = SubjectMaxLength, DefaultJson = "\"\"", ExampleJson = "\"Your code is {code}\"" },
                new(
                    "/properties/body",
                    SchemaKind.Text,
                    true,
                    Description: "The message text. A {name} is replaced by the argument of that name, "
                    + "left to right, and a substituted value is never re-scanned."
                ) {
                    MinLength = 1,
                    MaxLength = CommunicationServices.BodyMaxLength,
                    ExampleJson = "\"Your verification code is {code}. It expires in {minutes} minutes.\""
                },
                new(
                    "/properties/variables",
                    SchemaKind.Array,
                    Description: "The placeholders a send must supply. A send missing one is refused before "
                    + "any carrier is called."
                ) { ElementKind = SchemaKind.Text, Pattern = VariablePattern, ExampleJson = """["code"]""" },
                new(
                    "/properties/optionalVariables",
                    SchemaKind.Array,
                    Description: "The placeholders a send may supply. An unsupplied one is left as written, "
                    + "so a tenant testing the template sees it."
                ) { ElementKind = SchemaKind.Text, Pattern = VariablePattern, ExampleJson = """["minutes"]""" }
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(static x => x.JsonPointer)];

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="channel">Which channel.</param>
    /// <param name="body">The text.</param>
    /// <param name="subject">The subject line.</param>
    /// <param name="locale">The locale.</param>
    /// <param name="variables">The required placeholders.</param>
    /// <param name="optionalVariables">The optional placeholders.</param>
    /// <param name="location">The region.</param>
    public static string Body(
        string channel = "email",
        string body = "Your verification code is {code}.",
        string subject = "Your code",
        string locale = DefaultLocale,
        ImmutableArray<string> variables = default,
        ImmutableArray<string> optionalVariables = default,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["channel"] = channel,
                ["locale"] = locale,
                ["subject"] = subject,
                ["body"] = body,
                ["variables"] = new JsonArray(
                    [.. (variables.IsDefault ? ["code"] : variables).Select(static x => (JsonNode?)x)]
                ),
                ["optionalVariables"] = new JsonArray(
                    [.. (optionalVariables.IsDefault ? [] : optionalVariables).Select(static x => (JsonNode?)x)]
                )
            }
        }.ToJsonString();

    /// <summary>The channel a body is written for.</summary>
    public static ChannelKind ChannelOf(JsonElement desired) =>
        ChannelKinds.Parse(Bodies.Text(Bodies.Property(desired, "channel"), string.Empty));

    /// <summary>The id of the template's grain, derived from its address.</summary>
    public static Guid TemplateIdOf(ResourceId id) =>
        CommunicationGrainKeys.ResourceIdFor(id.TenantId, id.CanonicalPath);

    /// <summary>The parameters a body declares — required ones first, then optional.</summary>
    public static ImmutableArray<TemplateParameter> ParametersOf(JsonElement desired) {
        var parameters = ImmutableArray.CreateBuilder<TemplateParameter>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in Bodies.Strings(Bodies.Property(desired, "variables"))) {
            if (seen.Add(name)) {
                parameters.Add(new() { Name = name, Required = true });
            }
        }

        foreach (var name in Bodies.Strings(Bodies.Property(desired, "optionalVariables"))) {
            if (seen.Add(name)) {
                parameters.Add(new() { Name = name, Required = false });
            }
        }

        return parameters.ToImmutable();
    }

    /// <summary>The one localized body a desired body carries.</summary>
    public static LocalizedBody BodyOf(JsonElement desired) =>
        new() {
            Locale = Bodies.Text(Bodies.Property(desired, "locale"), DefaultLocale),
            Subject = Bodies.Text(Bodies.Property(desired, "subject"), string.Empty),
            Body = Bodies.Text(Bodies.Property(desired, "body"), string.Empty)
        };

    /// <summary>
    ///     A version built from the body alone, for <c>render</c> — numbered zero, because it is
    ///     what the body says and not what the grain has recorded.
    /// </summary>
    public static MessageTemplateVersion VersionOf(JsonElement desired) =>
        new() {
            Version = 0, Channel = ChannelOf(desired), Parameters = ParametersOf(desired), Bodies = [BodyOf(desired)]
        };

    /// <summary>Whether a recorded version carries what the body asks for.</summary>
    /// <param name="version">A version the grain holds — the newest, when checking convergence.</param>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     ⚠ Compared on content and channel, never on the version number: the number is the
    ///     grain's, and a body that changed and changed back is two versions with equal content.
    /// </remarks>
    public static bool Matches(MessageTemplateVersion version, JsonElement desired) {
        ArgumentNullException.ThrowIfNull(version);

        var bodies = version.Bodies.IsDefault ? [] : version.Bodies;
        var parameters = version.Parameters.IsDefault ? [] : version.Parameters;

        return version.Channel == ChannelOf(desired)
            && bodies.Length == 1
            && bodies[0] == BodyOf(desired)
            && parameters.SequenceEqual(ParametersOf(desired));
    }

    // ── The render action ──────────────────────────────────────────────────────────────────────

    /// <summary><c>POST …/templates/{name}/render</c>. What a send with these arguments would say.</summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Pure over the resource's own body, and that is what lets it run on the request
    ///         path.
    ///     </b> A synchronous action runs inside <c>ResourceManagerService</c>, which in
    ///     production is the gateway; this one reaches no grain, because <c>TemplateRenderer</c> is a
    ///     pure function and the body is in <c>ActionContext.Desired</c>. It renders what the resource
    ///     <i>says</i>, which after convergence is what the grain's newest version holds — and before
    ///     convergence is what it is about to hold, which is the more useful answer to "what will this
    ///     look like".
    /// </remarks>
    public const string RenderAction = "render";

    /// <summary>What a <c>render</c> carries.</summary>
    public static ResourceSchema RenderRequest { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/arguments",
                    SchemaKind.Array,
                    Description: "The arguments, one name=value per element. A missing required variable is "
                    + "refused, naming every missing one at once."
                ) {
                    ElementKind = SchemaKind.Text,
                    Pattern = CommunicationServices.ArgumentPattern,
                    ExampleJson = """["code=482913"]"""
                }
            ]
        );

    /// <summary>What a <c>render</c> returns.</summary>
    public static ResourceSchema RenderResponse { get; } =
        ResourceSchema.Of(
            [
                new("/subject", SchemaKind.Text, true, Description: "The subject, substituted."),
                new("/body", SchemaKind.Text, true, Description: "The body, substituted."),
                new("/locale", SchemaKind.Text, true, Description: "The locale it was rendered in.")
            ]
        );

    /// <summary>The <see cref="RenderResponse" /> body for a rendering.</summary>
    public static string RenderedJson(RenderedMessage rendered) {
        ArgumentNullException.ThrowIfNull(rendered);

        return new JsonObject { ["subject"] = rendered.Subject, ["body"] = rendered.Body, ["locale"] = rendered.Locale }
            .ToJsonString();
    }
}
