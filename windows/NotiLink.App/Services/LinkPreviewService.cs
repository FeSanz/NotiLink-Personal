using System.Net.Http;
using System.Text.RegularExpressions;

namespace NotiLink.App.Services;

public sealed record LinkPreview(string? Title, string? Description, string? ImageUrl);

/// <summary>
/// Best-effort Open Graph/title scraper for the optional "link preview" setting (off by
/// default — this reaches out to whatever URL a notification contains, so it only ever runs
/// when the user has explicitly opted in). Reads only the first chunk of the response (enough
/// for the &lt;head&gt;), never renders or executes anything from the page, and caches by URL
/// for the lifetime of the app so the same link is never re-fetched.
/// </summary>
internal static class LinkPreviewService
{
    private const int MaxBytesToRead = 200 * 1024;

    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(6),
    };

    private static readonly Dictionary<string, LinkPreview?> Cache = new();
    private static readonly SemaphoreSlim Lock = new(1, 1);

    static LinkPreviewService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("NotiLink/1.0 (+link preview)");
    }

    public static async Task<LinkPreview?> FetchAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return null;

        await Lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Cache.TryGetValue(url, out var cached)) return cached;
        }
        finally
        {
            Lock.Release();
        }

        LinkPreview? preview = null;
        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                var buffer = new byte[MaxBytesToRead];
                var totalRead = 0;
                int read;
                while (totalRead < buffer.Length &&
                       (read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead)).ConfigureAwait(false)) > 0)
                {
                    totalRead += read;
                }
                var html = System.Text.Encoding.UTF8.GetString(buffer, 0, totalRead);
                preview = ParseHtml(html, uri);
            }
        }
        catch (Exception)
        {
            // Timeout, DNS failure, malformed response, etc. — never surfaces as an error to
            // the user, the card just renders without a preview.
            preview = null;
        }

        await Lock.WaitAsync().ConfigureAwait(false);
        try
        {
            Cache[url] = preview;
        }
        finally
        {
            Lock.Release();
        }
        return preview;
    }

    private static LinkPreview? ParseHtml(string html, Uri pageUri)
    {
        string? Meta(string property) =>
            Regex.Match(html, $"""<meta[^>]+(?:property|name)=["']{Regex.Escape(property)}["'][^>]+content=["']([^"']*)["']""", RegexOptions.IgnoreCase) is { Success: true } m1
                ? System.Net.WebUtility.HtmlDecode(m1.Groups[1].Value)
                : Regex.Match(html, $"""<meta[^>]+content=["']([^"']*)["'][^>]+(?:property|name)=["']{Regex.Escape(property)}["']""", RegexOptions.IgnoreCase) is { Success: true } m2
                    ? System.Net.WebUtility.HtmlDecode(m2.Groups[1].Value)
                    : null;

        var title = Meta("og:title");
        if (string.IsNullOrWhiteSpace(title))
        {
            var titleMatch = Regex.Match(html, "<title[^>]*>([^<]*)</title>", RegexOptions.IgnoreCase);
            title = titleMatch.Success ? System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim()) : null;
        }

        var description = Meta("og:description") ?? Meta("description");
        var image = Meta("og:image");
        if (!string.IsNullOrWhiteSpace(image) && Uri.TryCreate(pageUri, image, out var absoluteImage))
            image = absoluteImage.ToString();

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(image))
            return null;

        return new LinkPreview(
            string.IsNullOrWhiteSpace(title) ? null : Truncate(title, 120),
            string.IsNullOrWhiteSpace(description) ? null : Truncate(description, 200),
            string.IsNullOrWhiteSpace(image) ? null : image);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
