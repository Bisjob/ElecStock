using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Components;

namespace ElecStock.UI.Services;

/// <summary>
/// Renders sheets and notes. Raw HTML is not rendered. Links to another sheet (<c>IRLB8721.md</c>)
/// open that part; other relative links pointed into the old stock folder and are shown as text.
/// </summary>
public static class Markdown
{
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static MarkupString ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return new MarkupString("");
        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        foreach (var link in document.Descendants<LinkInline>().ToList())
            Rewrite(link);
        return new MarkupString(document.ToHtml(Pipeline));
    }

    static void Rewrite(LinkInline link)
    {
        var url = link.Url ?? "";
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https" or "mailto")
        {
            if (!link.IsImage)
            {
                link.GetAttributes().AddPropertyIfNotExist("target", "_blank");
                link.GetAttributes().AddPropertyIfNotExist("rel", "noopener");
            }
            return;
        }
        if (url.StartsWith('#')) return;

        var path = url.Split('#', '?')[0];
        if (!link.IsImage && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            link.Url = "stock?ref=" + Uri.EscapeDataString(Path.GetFileNameWithoutExtension(path));
            return;
        }
        link.Url = null;
        link.GetAttributes().AddPropertyIfNotExist("title", $"{url} (file of the stock folder, not on the server)");
        link.GetAttributes().AddClass("dead-link");
    }

    /// <summary>Template of a new technical sheet.</summary>
    public static string SheetTemplate(string reference, string designation) => $"""
        # {reference} – {designation}

        **Type** · **Package** · **Manufacturer**

        ## Key characteristics

        | | |
        |---|---|
        | | |

        ## Pinout

        As seen from the front / top.

        ## Used in

        ## Gotchas

        ## Links

        """;
}
