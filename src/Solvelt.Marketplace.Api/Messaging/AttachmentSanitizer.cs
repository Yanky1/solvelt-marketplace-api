using System.Text;

namespace Solvelt.Marketplace.Api.Messaging;

/// <summary>
/// What a seller's attachment must pass before we keep it (decision M5-13): the type is decided from the BYTES (JPEG, PNG, WebP, PDF; the declared type and name are ignored), at most 5 MB,
/// image metadata (EXIF, XMP, text chunks, comments, thumbnails of GPS) is removed by rewriting the container, and a PDF with active content is refused. A file that is not exactly what its
/// bytes say (a truncated image, a bad chunk length) is refused, not repaired.
/// </summary>
public static class AttachmentSanitizer
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public sealed record Result(string? ContentType, byte[]? Bytes, string? Rejected);

    public static Result Process(byte[] input)
    {
        if (input.Length == 0) return Reject("empty");
        if (input.Length > MaxBytes) return Reject("too_large");
        if (input.Length >= 3 && input[0] == 0xFF && input[1] == 0xD8 && input[2] == 0xFF) return Wrap("image/jpeg", StripJpeg(input));
        if (input.Length >= 8 && input[0] == 0x89 && input[1] == 0x50 && input[2] == 0x4E && input[3] == 0x47 && input[4] == 0x0D && input[5] == 0x0A && input[6] == 0x1A && input[7] == 0x0A) return Wrap("image/png", StripPng(input));
        if (input.Length >= 12 && input[0] == 'R' && input[1] == 'I' && input[2] == 'F' && input[3] == 'F' && input[8] == 'W' && input[9] == 'E' && input[10] == 'B' && input[11] == 'P') return Wrap("image/webp", StripWebp(input));
        if (input.Length >= 5 && input[0] == '%' && input[1] == 'P' && input[2] == 'D' && input[3] == 'F' && input[4] == '-') return HasActiveContent(input) ? Reject("pdf_active_content") : new Result("application/pdf", input, null);
        return Reject("unsupported_type");
    }

    private static Result Reject(string why) => new(null, null, why);
    private static Result Wrap(string type, byte[]? bytes) => bytes is null ? Reject("malformed_" + type[6..]) : new Result(type, bytes, null);

    /// <summary>Keeps JFIF, ICC profile and Adobe markers (colour) and the image data; drops every other APPn (EXIF, XMP, IPTC...) and comments.</summary>
    internal static byte[]? StripJpeg(byte[] b)
    {
        var o = new MemoryStream(b.Length);
        o.Write(b, 0, 2);   // SOI
        var i = 2;
        while (i + 4 <= b.Length)
        {
            if (b[i] != 0xFF) return null;
            var marker = b[i + 1];
            if (marker == 0xFF) { i++; continue; }                       // fill byte
            if (marker == 0xD9) { o.Write(b, i, 2); return o.ToArray(); }   // EOI
            if (marker is >= 0xD0 and <= 0xD7 or 0x01) { o.Write(b, i, 2); i += 2; continue; }
            var len = (b[i + 2] << 8) | b[i + 3];
            if (len < 2 || i + 2 + len > b.Length) return null;
            if (marker == 0xDA)                                           // start of scan: the rest is entropy-coded data up to EOI, copied as it is
            {
                o.Write(b, i, b.Length - i);
                return b.Length >= 2 && b[^2] == 0xFF && b[^1] == 0xD9 ? o.ToArray() : null;
            }
            var drop = marker == 0xFE || (marker is >= 0xE1 and <= 0xEF && marker is not (0xE2 or 0xEE));
            if (!drop) o.Write(b, i, 2 + len);
            i += 2 + len;
        }
        return null;
    }

    private static readonly HashSet<string> PngKeep = ["IHDR", "PLTE", "IDAT", "IEND", "tRNS", "gAMA", "cHRM", "sRGB", "iCCP", "sBIT", "pHYs", "acTL", "fcTL", "fdAT", "cICP"];

    /// <summary>Keeps the chunks needed to show the picture; drops tEXt, zTXt, iTXt, eXIf, tIME and every unknown ancillary chunk.</summary>
    internal static byte[]? StripPng(byte[] b)
    {
        var o = new MemoryStream(b.Length);
        o.Write(b, 0, 8);
        var i = 8;
        var sawEnd = false;
        while (i + 12 <= b.Length)
        {
            var len = (long)((b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]);
            if (len < 0 || i + 12 + len > b.Length) return null;
            var type = Encoding.ASCII.GetString(b, i + 4, 4);
            if (PngKeep.Contains(type)) o.Write(b, i, (int)(12 + len));
            else if ((b[i + 4] & 0x20) == 0) return null;                  // an unknown CRITICAL chunk: not something we can vouch for
            i += (int)(12 + len);
            if (type == "IEND") { sawEnd = true; break; }
        }
        return sawEnd ? o.ToArray() : null;
    }

    /// <summary>Drops the EXIF and XMP chunks, clears their flags in VP8X and rewrites the RIFF size.</summary>
    internal static byte[]? StripWebp(byte[] b)
    {
        var declared = (long)(b[4] | (b[5] << 8) | (b[6] << 16) | (b[7] << 24)) + 8;
        if (declared > b.Length || declared < 12) return null;
        var body = new MemoryStream();
        var i = 12;
        while (i + 8 <= declared)
        {
            var size = (long)(b[i + 4] | (b[i + 5] << 8) | (b[i + 6] << 16) | (b[i + 7] << 24));
            var padded = size + (size & 1);
            if (i + 8 + padded > declared) return null;
            var fourcc = Encoding.ASCII.GetString(b, i, 4);
            if (fourcc is not ("EXIF" or "XMP "))
            {
                var chunk = b.AsSpan(i, (int)(8 + padded)).ToArray();
                if (fourcc == "VP8X" && size >= 10) chunk[8] &= unchecked((byte)~0x0C);   // no EXIF (0x08) or XMP (0x04) flag any more
                body.Write(chunk, 0, chunk.Length);
            }
            i += (int)(8 + padded);
        }
        if (i != declared) return null;
        var o = new MemoryStream();
        o.Write("RIFF"u8);
        var total = 4 + body.Length;
        o.Write([(byte)total, (byte)(total >> 8), (byte)(total >> 16), (byte)(total >> 24)]);
        o.Write("WEBP"u8);
        body.WriteTo(o);
        return o.ToArray();
    }

    private static readonly string[] ActiveTokens = ["/JavaScript", "/JS", "/Launch", "/EmbeddedFile", "/AA", "/RichMedia", "/OpenAction"];

    /// <summary>Best effort (object streams can hide tokens, which is why files are only ever served as attachments): the name escapes <c>#xx</c> are decoded first so <c>/Java#53cript</c> does not slip by.</summary>
    internal static bool HasActiveContent(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '#' && i + 2 < text.Length && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2])) { sb.Append((char)Convert.ToInt32(text.Substring(i + 1, 2), 16)); i += 2; }
            else sb.Append(text[i]);
        }
        var decoded = sb.ToString();
        foreach (var t in ActiveTokens)
        {
            var at = 0;
            while ((at = decoded.IndexOf(t, at, StringComparison.Ordinal)) >= 0)
            {
                var end = at + t.Length;
                // a PDF name ends at a delimiter or white space: "/JS" must not match "/JSON"
                if (end >= decoded.Length || !char.IsAsciiLetterOrDigit(decoded[end])) return true;
                at = end;
            }
        }
        return false;
    }
}
