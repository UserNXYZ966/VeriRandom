using System;
using System.Text;

namespace SecRandom.Core.Models.SlideTrigger;

/// <summary>
///     One marker a teacher can place in a deck: a short id (what the image carries) plus the parameters (what the
///     app does when that id is seen).
/// </summary>
/// <remarks>
///     Consumption is recorded per session rather than by a one-shot flag, because a deck is reused all term: slide 12
///     should fire once per lesson, not once for ever. Rolling the session (a new lesson, or an explicit
///     "start a new session" action) is what makes the same marker usable again.
/// </remarks>
public sealed class SlideTriggerDefinition
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Teacher-facing name, so a list of markers is not just ids.</summary>
    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public SlideTriggerParameters Parameters { get; set; } = new();

    /// <summary>Session in which this marker last fired; null means it has never fired.</summary>
    public string? LastConsumedSession { get; set; }

    public DateTimeOffset? LastConsumedAt { get; set; }

    /// <summary>True when this marker already fired in <paramref name="session" /> and must be ignored.</summary>
    public bool IsConsumedIn(string session) =>
        !string.IsNullOrEmpty(LastConsumedSession)
        && string.Equals(LastConsumedSession, session, StringComparison.Ordinal);

    public void MarkConsumed(string session, DateTimeOffset at)
    {
        LastConsumedSession = session;
        LastConsumedAt = at;
    }
}

/// <summary>
///     The id written into a marker image.
/// </summary>
/// <remarks>
///     Ten Crockford-style characters: no <c>I</c>/<c>L</c>/<c>O</c>/<c>U</c>, so a misread is impossible rather than
///     merely unlikely once the text has been through a QR round trip and a human's eyes. Sixty bits is far more than
///     a classroom deck needs and keeps the marker matrix small.
/// </remarks>
public static class SlideTriggerId
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int IdLength = 10;

    public static string New() => FromBytes(Guid.NewGuid().ToByteArray());

    public static bool IsValid(string? value)
    {
        if (value is not { Length: IdLength })
            return false;

        foreach (var character in value)
            if (Alphabet.IndexOf(char.ToUpperInvariant(character)) < 0)
                return false;

        return true;
    }

    private static string FromBytes(byte[] bytes)
    {
        var builder = new StringBuilder(IdLength);

        // 5 bits per character, taken from the leading bytes; 60 bits is all we need and the rest is discarded.
        var buffer = 0;
        var bits = 0;

        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;

            while (bits >= 5 && builder.Length < IdLength)
            {
                bits -= 5;
                builder.Append(Alphabet[(buffer >> bits) & 0x1F]);
            }

            if (builder.Length == IdLength)
                break;
        }

        return builder.ToString();
    }
}
