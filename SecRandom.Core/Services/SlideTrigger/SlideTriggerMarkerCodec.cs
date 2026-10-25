using System;
using System.Text;
using SecRandom.Core.Models.SlideTrigger;

namespace SecRandom.Core.Services.SlideTrigger;

/// <summary>
///     The text carried by a courseware marker image.
/// </summary>
/// <remarks>
///     <para>
///         The payload is deliberately only a prefix and an id. Everything the marker does lives in the app, so a
///         teacher who changes a marker from "one student" to "three" does not have to regenerate the PNG, re-paste it
///         into the deck, and re-save the file. It also keeps the encoded matrix small, which is what makes decoding a
///         screenshot reliable in the first place.
///     </para>
///     <para>
///         Parsing is forgiving about separators and case because the text arrives through an image: an id that reads
///         exactly the same to a human should not be rejected because a decoder normalised the case or a hand-typed
///         copy used a dash. It is strict about the id itself — fixed length and a confusion-free alphabet — so a
///         random sentence on a slide cannot accidentally parse as a marker.
///     </para>
/// </remarks>
public static class SlideTriggerMarkerCodec
{
    /// <summary>Format prefix. Bumping it is what retires older markers on purpose.</summary>
    public const string Prefix = "VRTRIG1";

    private const int IdLength = 10;

    private static readonly char[] Separators = [':', '.', '-', '_', ' ', '\t', '\r', '\n', '/', '|'];

    public static string Encode(string id)
    {
        if (!SlideTriggerId.IsValid(id))
            throw new ArgumentException($"'{id}' is not a slide trigger id.", nameof(id));

        return $"{Prefix}:{id}";
    }

    /// <summary>
    ///     Extracts the id from decoded marker text. Returns false for anything that is not a marker, which is the
    ///     common case: a screenshot of a slide usually contains no marker at all.
    /// </summary>
    public static bool TryParse(string? text, out string id)
    {
        id = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var compact = Compact(text);

        if (compact.StartsWith(Prefix, StringComparison.Ordinal))
            compact = compact[Prefix.Length..].TrimStart(Separators);

        // A bare id is accepted: the prefix is a discriminator, not a secret, and a marker whose prefix pixel blob
        // was lost to a projector's crop should still fire rather than silently do nothing.
        if (compact.Length != IdLength)
            return false;

        var candidate = compact.ToUpperInvariant();
        if (!SlideTriggerId.IsValid(candidate))
            return false;

        id = candidate;
        return true;
    }

    /// <summary>True when the text looks like a marker at all; used to skip the id validation path cheaply.</summary>
    public static bool LooksLikeMarker(string? text) => TryParse(text, out _);

    private static string Compact(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var character in text)
            if (Array.IndexOf(Separators, character) < 0)
                builder.Append(character);

        return builder.ToString();
    }
}
