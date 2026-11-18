using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Html.Layout;

namespace PdfNetStack.Html;

/// <summary>
/// Fetches style sheets, images and fonts for a conversion. By default only data: URIs and
/// files inside the base directory load: nothing is fetched from the network unless the caller
/// supplies a loader, so converting untrusted HTML cannot reach internal services or read files
/// elsewhere on the machine.
/// </summary>
internal sealed class Resources
{
    private const int MaxResourceBytes = 64 << 20;

    private readonly Uri? _base;
    private readonly Func<Uri, byte[]?>? _loader;
    private readonly Func<Uri, CancellationToken, Task<byte[]?>>? _asyncLoader;
    private readonly CancellationToken _cancellationToken;
    private readonly string? _directory;
    private readonly Dictionary<string, byte[]?> _bytes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HtmlImage?> _images = new(StringComparer.Ordinal);

    /// <param name="baseUri">The address relative URLs resolve against.</param>
    /// <param name="loader">Fetches by absolute URL; null loads only data: URIs and files under the base.</param>
    /// <param name="asyncLoader">Fetches by absolute URL with the conversion's token; used instead of <paramref name="loader"/> when set.</param>
    /// <param name="cancellationToken">The conversion's token, passed to <paramref name="asyncLoader"/> and observed before each fetch.</param>
    public Resources(Uri? baseUri, Func<Uri, byte[]?>? loader, Func<Uri, CancellationToken, Task<byte[]?>>? asyncLoader = null, CancellationToken cancellationToken = default)
    {
        _base = baseUri;
        _loader = loader;
        _asyncLoader = asyncLoader;
        _cancellationToken = cancellationToken;
        if (baseUri is { IsAbsoluteUri: true, IsFile: true })
        {
            string path = Path.GetFullPath(baseUri.LocalPath);
            _directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (_directory is not null && !_directory.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)) _directory += Path.DirectorySeparatorChar;
        }
    }

    /// <summary>The bytes behind a URL from the document, or null when it cannot or may not be loaded.</summary>
    public byte[]? Load(string url)
    {
        url = url.Trim();
        if (url.Length == 0) return null;
        if (_bytes.TryGetValue(url, out byte[]? cached)) return cached;
        byte[]? data = url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? DataUri(url) : Fetch(url);
        if (data is { Length: > MaxResourceBytes }) data = null;
        _bytes[url] = data;
        return data;
    }

    private byte[]? Fetch(string url)
    {
        Uri? uri = Uri.TryCreate(url, UriKind.Absolute, out Uri? absolute) ? absolute
            : _base is not null && Uri.TryCreate(_base, url, out Uri? relative) ? relative
            : null;
        if (uri is null) return null;
        _cancellationToken.ThrowIfCancellationRequested();

        // The conversion runs on a thread of its own with no synchronization context, so waiting
        // here for the fetch blocks only that thread and cannot deadlock: the fetch completes on
        // the thread pool, and the token cancels it.
        if (_asyncLoader is not null) return _asyncLoader(uri, _cancellationToken).GetAwaiter().GetResult();
        if (_loader is not null) return _loader(uri);
        if (!uri.IsFile || _directory is null) return null;

        // Only files under the base directory, after resolving any ".." in the path.
        string path = Path.GetFullPath(uri.LocalPath);
        if (!path.StartsWith(_directory, OperatingSystem() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxResourceBytes ? File.ReadAllBytes(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool OperatingSystem() => Path.DirectorySeparatorChar == '\\';

    /// <summary>A data: URI (RFC 2397): base64 or percent-encoded.</summary>
    internal static byte[]? DataUri(string url)
    {
        int comma = url.IndexOf(',');
        if (comma < 0) return null;
        string header = url.Substring(5, comma - 5);
        string payload = url.Substring(comma + 1);
        if (header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return Convert.FromBase64String(Uri.UnescapeDataString(payload).Replace(" ", "").Replace("\n", "").Replace("\r", ""));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
    }

    /// <summary>
    /// The absolute form of a URL written in a style sheet: resolved against <paramref name="sheet"/>,
    /// the sheet's own absolute URL, or against the document's base when that is null. Absolute
    /// URLs, data: URIs and fragments come back as they are; null when there is no base.
    /// </summary>
    public string? Resolve(string url, string? sheet)
    {
        string trimmed = url.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || Uri.TryCreate(trimmed, UriKind.Absolute, out _)) return trimmed;
        Uri? against = sheet is not null && Uri.TryCreate(sheet, UriKind.Absolute, out Uri? own) ? own : _base;
        return against is not null && Uri.TryCreate(against, trimmed, out Uri? resolved) ? resolved.AbsoluteUri : null;
    }

    /// <summary>A style sheet's text, decoded as UTF-8 unless a byte order mark says otherwise.</summary>
    public string? LoadText(string url)
    {
        byte[]? data = Load(url);
        if (data is null) return null;
        using var reader = new StreamReader(new MemoryStream(data), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>An image: JPEG, PNG, GIF or SVG; null when it is missing or not an image the library reads.</summary>
    public HtmlImage? LoadImage(string url)
    {
        if (_images.TryGetValue(url, out HtmlImage? cached)) return cached;
        HtmlImage? image = null;
        if (Load(url) is byte[] data)
        {
            try
            {
                image = PdfSvg.Sniff(data) ? new HtmlImage(PdfSvg.Load(data)) : new HtmlImage(PdfImage.Load(data));
            }
            catch (Exception e) when (e is FormatException or NotSupportedException or ArgumentException or InvalidDataException or EndOfStreamException)
            {
                image = null;
            }
        }

        _images[url] = image;
        return image;
    }
}
