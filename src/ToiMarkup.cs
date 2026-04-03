using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Renderers.Normalize;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.Text;

using Renderers = Markdig.Renderers;

namespace TaleOfImmortalTool;

// Markup Tags:
//
// <b>bold</b>
// <u>underline</u>
// <i>italic</i>
// <s>strikethrough</s>
// <sub>subscript</sub>
// <sup>superscript</sup>
//
// <size=180%><b>h1</b></size>
// <size=160%><b>h2</b></size>
// <size=140%><b>h3</b></size>
// <size=120%><b>h4</b></size>
// <size=110%><b>h5</b></size>
// <size=100%><b>h6</b></size>
//
// <color=#3f3f46><i>> <space=0.5em><b>quoted text</i><b></color>
// <color=#7c2d12>inline code</color>
// <color=#1558c0><u>http://example.com</u></color>
// <indent=2.5%>indented 2.5%</indent>
// <align="center">center aligned</align>
// <space=1em>padded text
// <voffset=1em>voffset</voffset>

public static class ToiMarkup
{
    public static string FromMarkdown(string markdown)
    {
        var pipeline = new MarkdownPipelineBuilder()
            .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
            .UseCjkFriendlyEmphasis()
            .Build();

        var document = Markdown.Parse(markdown, pipeline);

        var buffer = new StringBuilder();
        using (var writer = new StringWriter(buffer))
        {
            var renderer = ToiMarkupRenderer(writer);
            renderer.Render(document);
        }

        return buffer.ToString().TrimEnd();
    }

    // Doing this since I'm unsure how to use IMarkdownExtension API for modifying renderers
    static NormalizeRenderer ToiMarkupRenderer(TextWriter writer)
    {
        var renderer = new NormalizeRenderer(writer);

        renderer.ObjectRenderers
            .ReplaceOrAdd<Renderers.Normalize.CodeBlockRenderer>(new CodeBlockRenderer());
        renderer.ObjectRenderers
            .ReplaceOrAdd<Renderers.Normalize.HeadingRenderer>(new HeadingRenderer());
        renderer.ObjectRenderers
            .ReplaceOrAdd<Renderers.Normalize.QuoteBlockRenderer>(new QuoteBlockRenderer());
        renderer.ObjectRenderers
            .ReplaceOrAdd<Renderers.Normalize.HtmlBlockRenderer>(new HtmlBlockRenderer());

        renderer.ObjectRenderers
            .ReplaceOrAdd<Renderers.Normalize.Inlines.CodeInlineRenderer>(new CodeInlineRenderer());
        renderer.ObjectRenderers
            .ReplaceOrAdd<Renderers.Normalize.Inlines.EmphasisInlineRenderer>(new EmphasisInlineRenderer());
        renderer.ObjectRenderers
            .ReplaceOrAdd<Renderers.Normalize.Inlines.LineBreakInlineRenderer>(new LineBreakInlineRenderer());
        renderer.ObjectRenderers
            .ReplaceOrAdd<Renderers.Normalize.Inlines.LinkInlineRenderer>(new LinkInlineRenderer());
        renderer.ObjectRenderers
            .ReplaceOrAdd<Renderers.Normalize.Inlines.NormalizeHtmlInlineRenderer>(new NormalizeHtmlInlineRenderer());

        return renderer;
    }
}

class CodeBlockRenderer : NormalizeObjectRenderer<CodeBlock>
{
    protected override void Write(NormalizeRenderer renderer, CodeBlock obj)
    {
        renderer
            .Write("<color=#7c2d12>")
            .WriteLeafRawLines(obj, false, obj is not FencedCodeBlock)
            .Write("</color>");

        renderer.FinishBlock(true);
    }
}

class HeadingRenderer : NormalizeObjectRenderer<HeadingBlock>
{
    protected override void Write(NormalizeRenderer renderer, HeadingBlock obj)
    {
        var size = obj.Level switch
        {
            1 => "180%",
            2 => "160%",
            3 => "140%",
            4 => "120%",
            5 => "110%",
            _ => "100%"
        };

        renderer
            .Write($"<size={size}><b>")
            .WriteLeafInline(obj)
            .Write("</b></size>");

        renderer.FinishBlock(true);
    }
}

class QuoteBlockRenderer : NormalizeObjectRenderer<QuoteBlock>
{
    protected override void Write(NormalizeRenderer renderer, QuoteBlock obj)
    {
        renderer.PushIndent("");
        renderer.Write("<color=#3f3f46><i>> <space=0.5em>");
        renderer.WriteChildren(obj);
        renderer.Write("</i></color>");
        renderer.PopIndent();

        renderer.FinishBlock(true);
    }
}

class HtmlBlockRenderer : NormalizeObjectRenderer<HtmlBlock>
{
    protected override void Write(NormalizeRenderer renderer, HtmlBlock obj)
    {
        if (obj.Type == HtmlBlockType.Comment)
            return;

        renderer.WriteLeafRawLines(obj, true, false);
    }
}

class CodeInlineRenderer : NormalizeObjectRenderer<CodeInline>
{
    protected override void Write(NormalizeRenderer renderer, CodeInline obj)
    {
        renderer
            .Write("<color=#7c2d12>")
            .Write(obj.Content)
            .Write("</color>");
    }
}

class EmphasisInlineRenderer : NormalizeObjectRenderer<EmphasisInline>
{
    protected override void Write(NormalizeRenderer renderer, EmphasisInline obj)
    {
        string tag = obj.DelimiterChar switch
        {
            '*' or '_' when obj.DelimiterCount == 2 => "b",
            '*' or '_' => "i",
            '~' => "s",
            _ => "i"
        };

        renderer.Write($"<{tag}>");
        renderer.WriteChildren(obj);
        renderer.Write($"</{tag}>");
    }
}

class LineBreakInlineRenderer : NormalizeObjectRenderer<LineBreakInline>
{
    protected override void Write(NormalizeRenderer renderer, LineBreakInline obj)
    {
        if (obj.IsHard)
        {
            renderer.WriteLine();
        }
        else
        {
            renderer.Write(" ");
        }
    }
}

class LinkInlineRenderer : NormalizeObjectRenderer<LinkInline>
{
    protected override void Write(NormalizeRenderer renderer, LinkInline obj)
    {
        var url = obj.Url ?? "";
        if (string.IsNullOrWhiteSpace(url))
            return;

        if (obj.IsImage)
        {
            // ![](name)
            renderer.Write($"<sprite name=\"{url}\">");
        }
        else
        {
            renderer
                .Write("<color=#1558c0><u>")
                .Write(url)
                .Write("</u></color>");
        }
    }
}

class NormalizeHtmlInlineRenderer : NormalizeObjectRenderer<HtmlInline>
{
    static bool TryMapTag(ReadOnlySpan<char> tag, out string output)
    {
        static bool CheckTag(ReadOnlySpan<char> tag, string against, out bool isClosing)
        {
            isClosing = false;

            if (tag.Length < 3 || tag[0] != '<')
                return false;

            if (tag[1] == '/')
            {
                tag = tag[2..];
                isClosing = true;
            }
            else
            {
                tag = tag[1..];
            }

            return tag.StartsWith(against, StringComparison.OrdinalIgnoreCase);
        }

        output = "";

        if (CheckTag(tag, "ins", out var isClosing))
        {
            output = isClosing ? "</u>" : "<u>";
            return true;
        }

        if (CheckTag(tag, "sup", out isClosing))
        {
            output = isClosing ? "</sup>" : "<sup>";
            return true;
        }

        if (CheckTag(tag, "sub", out isClosing))
        {
            output = isClosing ? "</sub>" : "<sub>";
            return true;
        }

        return false;
    }

    protected override void Write(NormalizeRenderer renderer, HtmlInline obj)
    {
        if (TryMapTag(obj.Tag, out var tag))
        {
            renderer.Write(tag);
        }
        else
        {
            renderer.Write(obj.Tag);
        }
    }
}