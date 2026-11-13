using Microsoft.AspNetCore.Http;

namespace AbpCluster.Storage;

/// <summary>A kind of file known by its first bytes: its usual extension and its media type.</summary>
public sealed record FileKind(string Extension, string ContentType);

/// <summary>
/// What an uploaded file is, by its bytes rather than its name or the type the browser sent, and the headers that keep a
/// browser from treating a stored file as a page.
/// </summary>
public static class FileKinds
{
    public static readonly FileKind Png = new("png", "image/png");
    public static readonly FileKind Jpeg = new("jpg", "image/jpeg");
    public static readonly FileKind WebP = new("webp", "image/webp");
    public static readonly FileKind Gif = new("gif", "image/gif");
    public static readonly FileKind Pdf = new("pdf", "application/pdf");
    public static readonly FileKind Zip = new("zip", "application/zip");

    /// <summary>The images a page may show: PNG, JPEG, WebP, GIF.</summary>
    public static IReadOnlyList<FileKind> Images { get; } = [Png, Jpeg, WebP, Gif];

    /// <summary>The kind the bytes start as, or null for one this doesn't know.</summary>
    public static FileKind? Sniff(ReadOnlySpan<byte> b) =>
        b.Length >= 8 && b[..8].SequenceEqual((byte[])[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) ? Png
        : b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? Jpeg
        : b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8) ? WebP
        : b.Length >= 6 && (b[..6].SequenceEqual("GIF87a"u8) || b[..6].SequenceEqual("GIF89a"u8)) ? Gif
        : b.Length >= 5 && b[..5].SequenceEqual("%PDF-"u8) ? Pdf
        : b.Length >= 4 && b[..4].SequenceEqual((byte[])[0x50, 0x4B, 0x03, 0x04]) ? Zip
        : null;

    /// <summary>The kind the bytes start as when it's one of <paramref name="allowed"/>, else null.</summary>
    public static FileKind? Sniff(ReadOnlySpan<byte> bytes, IEnumerable<FileKind> allowed) =>
        Sniff(bytes) is { } kind && allowed.Contains(kind) ? kind : null;

    /// <summary>
    /// Headers for serving a stored file: the browser keeps to the type given (<c>nosniff</c>), and runs nothing in it
    /// even if it's opened on its own (a sandbox with nothing allowed). A download names its file.
    /// </summary>
    public static void ServeSafely(HttpResponse response, string? downloadName = null)
    {
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        if (downloadName is not null)
        {
            response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition { FileName = downloadName, DispositionType = "attachment" }.ToString();
        }
    }
}
