using System.Collections.Immutable;

namespace CyberCloud.Providers.Communication.Contracts;

/// <summary>
///     How a body spells a <see cref="ChannelKind" />, and the way back.
/// </summary>
/// <remarks>
///     ⚠ <b>Lower-case words, not the enum's own names, and declared once for every schema that
///     carries a channel.</b> A published api-version is immutable, so the five values here are a
///     contract: <c>SchemaProperty.AllowedValues</c> refuses a sixth at the write path and
///     <c>OpenApiCompatibility.EnumValueRemoved</c> refuses taking one away. <see cref="ChannelKind" />
///     has no sixth member either — chat is M3 and is a different shape, as that enum's remarks say —
///     so the two sets are the same five, checked by <c>CommunicationDeclarationTests</c>.
/// </remarks>
public static class ChannelKinds {
    /// <summary>The five, in the order docs/plan/17 § The channel abstraction lists them.</summary>
    public static ImmutableArray<string> AllowedValues { get; } = ["sms", "whatsapp", "email", "push", "voice"];

    /// <summary>The kind a body spells, or <see cref="ChannelKind.Unknown" /> for anything else.</summary>
    /// <param name="spelled">One of <see cref="AllowedValues" />, compared ordinally.</param>
    public static ChannelKind Parse(string? spelled) =>
        spelled switch {
            "sms" => ChannelKind.Sms,
            "whatsapp" => ChannelKind.WhatsApp,
            "email" => ChannelKind.Email,
            "push" => ChannelKind.Push,
            "voice" => ChannelKind.Voice,
            _ => ChannelKind.Unknown
        };

    /// <summary>The body spelling of a kind. Empty for <see cref="ChannelKind.Unknown" />.</summary>
    /// <param name="kind">The kind.</param>
    public static string Spell(ChannelKind kind) =>
        kind switch {
            ChannelKind.Sms => "sms",
            ChannelKind.WhatsApp => "whatsapp",
            ChannelKind.Email => "email",
            ChannelKind.Push => "push",
            ChannelKind.Voice => "voice",
            _ => string.Empty
        };
}
