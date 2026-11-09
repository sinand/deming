using System.Text;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Components;

namespace Deming.Server.Panel;

/// <summary>
/// Renders user-written markdown (finding bodies) to HTML that is safe to place in the page.
/// Raw HTML never survives: it is shown as escaped text. Only http and https links stay links,
/// anything else (javascript:, data:, relative paths) and every image is reduced to its text.
/// </summary>
public static class MarkdownView
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseReferralLinks("nofollow", "noopener", "noreferrer")
        .Build();

    public static MarkupString Render(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return new MarkupString("");

        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            if (link.IsImage || !IsWebUrl(link.Url)) Unlink(link);
        }
        foreach (var auto in document.Descendants<AutolinkInline>().ToList())
        {
            if (!IsWebUrl(auto.Url)) auto.ReplaceBy(new LiteralInline(auto.Url));
        }

        using var writer = new StringWriter(new StringBuilder());
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return new MarkupString(writer.ToString());
    }

    private static bool IsWebUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Replaces a link or image by its visible text (for an image, its alt text).</summary>
    private static void Unlink(LinkInline link)
    {
        var children = new List<Inline>();
        for (var child = link.FirstChild; child is not null;)
        {
            var next = child.NextSibling;
            child.Remove();
            children.Add(child);
            child = next;
        }

        if (children.Count == 0) children.Add(new LiteralInline(link.Url ?? ""));
        foreach (var child in children) link.InsertBefore(child);
        link.Remove();
    }
}
