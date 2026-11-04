using System.Text;
using SkiaSharp;

namespace Solvelt.Marketplace.Api.Messaging;

/// <summary>
/// What a seller's attachment must pass before we keep it (decisions M5-13, M5-22): the type is decided from the BYTES (JPEG, PNG, WebP, PDF; the declared type and name are ignored), at most
/// 5 MB, and an IMAGE is never kept as it came: it is decoded and a fresh image is encoded (SkiaSharp), so no metadata can survive whatever the container hides (EXIF, XMP, MPF and secondary
/// images, GPS, comments, trailing data). The EXIF orientation is applied first, the longer side is capped at <see cref="MaxLongSide"/> pixels and a picture of more than
/// <see cref="MaxPixels"/> pixels is refused before it is decoded (a decompression bomb). A file that does not decode is refused. A PDF with active content is refused.
/// </summary>
public static class AttachmentSanitizer
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int MaxLongSide = 4096;
    public const long MaxPixels = 40_000_000;
    public sealed record Result(string? ContentType, byte[]? Bytes, string? Rejected);

    public static Result Process(byte[] input)
    {
        if (input.Length == 0) return Reject("empty");
        if (input.Length > MaxBytes) return Reject("too_large");
        if (input.Length >= 3 && input[0] == 0xFF && input[1] == 0xD8 && input[2] == 0xFF) return Image(input, "image/jpeg", SKEncodedImageFormat.Jpeg);
        if (input.Length >= 8 && input[0] == 0x89 && input[1] == 0x50 && input[2] == 0x4E && input[3] == 0x47 && input[4] == 0x0D && input[5] == 0x0A && input[6] == 0x1A && input[7] == 0x0A) return Image(input, "image/png", SKEncodedImageFormat.Png);
        if (input.Length >= 12 && input[0] == 'R' && input[1] == 'I' && input[2] == 'F' && input[3] == 'F' && input[8] == 'W' && input[9] == 'E' && input[10] == 'B' && input[11] == 'P') return Image(input, "image/webp", SKEncodedImageFormat.Webp);
        if (input.Length >= 5 && input[0] == '%' && input[1] == 'P' && input[2] == 'D' && input[3] == 'F' && input[4] == '-') return HasActiveContent(input) ? Reject("pdf_active_content") : new Result("application/pdf", input, null);
        return Reject("unsupported_type");
    }

    private static Result Reject(string why) => new(null, null, why);

    private static Result Image(byte[] input, string type, SKEncodedImageFormat format)
    {
        var malformed = Reject("malformed_" + type[6..]);
        try
        {
            using var data = SKData.CreateCopy(input);
            using var codec = SKCodec.Create(data);
            if (codec is null) return malformed;
            var (w, h) = (codec.Info.Width, codec.Info.Height);
            if (w <= 0 || h <= 0) return malformed;
            if ((long)w * h > MaxPixels) return Reject("image_too_large");   // decided from the header, before a single pixel is decoded
            using var decoded = SKBitmap.Decode(codec);
            if (decoded is null) return malformed;
            var origin = codec.EncodedOrigin;
            var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            int ow = swap ? h : w, oh = swap ? w : h;                       // size after the orientation is applied
            var scale = Math.Max(ow, oh) > MaxLongSide ? (float)MaxLongSide / Math.Max(ow, oh) : 1f;
            int dw = Math.Max(1, (int)Math.Round(ow * scale)), dh = Math.Max(1, (int)Math.Round(oh * scale));
            using var dest = new SKBitmap(new SKImageInfo(dw, dh, SKColorType.Rgba8888, format == SKEncodedImageFormat.Jpeg ? SKAlphaType.Opaque : SKAlphaType.Unpremul));
            using (var canvas = new SKCanvas(dest))
            {
                if (format == SKEncodedImageFormat.Jpeg) canvas.Clear(SKColors.White);
                canvas.Scale(scale);
                canvas.Concat(OrientationMatrix(origin, w, h));
                using var image = SKImage.FromBitmap(decoded);
                canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), null);
            }
            using var fresh = SKImage.FromBitmap(dest);
            using var encoded = fresh.Encode(format, 85);
            if (encoded is null) return malformed;
            var bytes = encoded.ToArray();
            return bytes.Length > MaxBytes ? Reject("too_large") : new Result(type, bytes, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return malformed; }
    }

    /// <summary>Maps the stored pixels to the way the picture is meant to be seen (EXIF orientation 1-8); <paramref name="w"/> and <paramref name="h"/> are the stored size.</summary>
    internal static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int w, int h) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
        _ => SKMatrix.Identity,
    };

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
