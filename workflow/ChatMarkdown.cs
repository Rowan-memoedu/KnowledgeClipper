using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace KnowledgeClipper.Workflow;

public sealed record ChatRenderedCard(string FrontHtml, string BackHtml, RemNoteCardPlan Card, ImageAsset[] Media);
public sealed record ChatCopyMath(string Tex, string Markdown, bool Block);

// A shared AST keeps code/escapes out of math parsing. Source messages are never normalized in place.
public static class ChatMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseMathematics().UsePreciseSourceLocation().DisableHtml().Build();
    private static readonly MarkdownPipeline CodeSpans = new MarkdownPipelineBuilder().UsePreciseSourceLocation().DisableHtml().Build();
    private sealed record Row(RemNoteRichPart[] Parts, int Parent, string? Html = null);

    public static void ValidateAssets(string markdown, IReadOnlyList<ImageAsset> assets) => Parse(markdown, assets);

    // Match actual image nodes in reading order, including registered display URLs.
    // Hashes mentioned in code or ordinary prose are not image attachments.
    public static ImageAsset[] ReferencedImages(string markdown, IReadOnlyList<ImageAsset> assets)
    {
        var document = Markdown.Parse(NormalizeMediaReferences(markdown, assets), Pipeline);
        return document.Descendants().OfType<LinkInline>().Where(link => link.IsImage).Select(link =>
        {
            var url = link.Url ?? "";
            var asset = url.StartsWith("asset:", StringComparison.Ordinal)
                ? assets.SingleOrDefault(a => a.Sha256 == url[6..]) : null;
            if (asset == null) throw new InvalidDataException("图片未登记或缺失：" + url);
            asset.ReadVerified(); return asset;
        }).DistinctBy(a => a.Sha256).ToArray();
    }

    // Display URLs refer only to already registered local bytes; never fetch them as remote media.
    public static string NormalizeMediaReferences(string source, IReadOnlyList<ImageAsset> assets)
    {
        if (!source.Contains("https://clipper-media.local/", StringComparison.Ordinal)) return source;
        var ast = Markdown.Parse(source, CodeSpans);
        var protectedSpans = ast.Descendants().Where(n => n is CodeInline or CodeBlock or LinkInline or AutolinkInline).Select(n => n.Span).ToArray();
        var edits = new List<(int Start, int Length, string Value)>();
        string? Reference(string url)
        {
            var match = Regex.Match(url, @"\Ahttps://clipper-media\.local/([a-f0-9]{64})\.png\z");
            if (!match.Success) return null;
            var sha = match.Groups[1].Value;
            var asset = assets.FirstOrDefault(a => a.Sha256 == sha) ?? throw new InvalidDataException("图片未登记或缺失：" + url);
            asset.ReadVerified();
            return "![图片](asset:" + sha + ")";
        }
        foreach (var link in ast.Descendants().OfType<LinkInline>())
            if (Reference(link.Url ?? "") is { } reference)
                edits.Add((link.Span.Start, link.Span.Length, reference));
        foreach (var link in ast.Descendants().OfType<AutolinkInline>())
            if (Reference(link.Url) is { } reference)
                edits.Add((link.Span.Start, link.Span.Length, reference));
        foreach (Match match in Regex.Matches(source, @"(?<![\w/])https://clipper-media\.local/[a-f0-9]{64}\.png(?![\w./?\#:%-])"))
            if (!protectedSpans.Any(s => match.Index >= s.Start && match.Index <= s.End))
                edits.Add((match.Index, match.Length, Reference(match.Value)!));
        var result = new StringBuilder(source);
        foreach (var edit in edits.OrderByDescending(e => e.Start)) result.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Value);
        return result.ToString();
    }

    public static string BackWithAnnotation(string back, string annotation) => string.IsNullOrWhiteSpace(annotation)
        ? back : back + "\n\n---\n\n批注：\n\n" + annotation;

    public static ChatRenderedCard Render(string front, string back, IReadOnlyList<ImageAsset> assets, string annotation = "", string frontAnnotation = "")
    {
        var f = Parse(front, assets); var b = Parse(back, assets);
        var frontNotes = string.IsNullOrWhiteSpace(frontAnnotation) ? [] : Parse(frontAnnotation, assets);
        var frontBody = f.ToList();
        if (frontNotes.Count > 0)
        {
            // RemNote's question is one rich-text Rem; its children are answers.
            // Keep question annotations on that same front, never as answer children.
            f.Add(new([new("text", "批注：")], -1));
            f.AddRange(frontNotes);
        }
        var notes = string.IsNullOrWhiteSpace(annotation) ? [] : Parse(annotation, assets);
        var body = b.ToList();
        if (notes.Count > 0)
        {
            var heading = b.Count; b.Add(new([new("text", "批注")], -1));
            b.AddRange(notes.Select(row => new Row(row.Parts, row.Parent < 0 ? heading : heading + 1 + row.Parent)));
        }
        if (f.Count == 0 || b.Count == 0) throw new InvalidDataException("卡片正反面都需要内容。");
        var frontParts = f.SelectMany((row, i) => i == 0 ? row.Parts : new[] { new RemNoteRichPart("text", "\n") }.Concat(row.Parts)).ToArray();
        var allParts = frontParts.Concat(b.SelectMany(r => r.Parts)).ToArray();
        var used = assets.Where(a => allParts.Any(p => p.Kind == "image" && p.Sha256 == a.Sha256)).DistinctBy(a => a.Sha256).ToArray();
        string Html(List<Row> rows) => string.Join("<br>", rows.Select(row =>
        {
            var depth = 0; var parent = row.Parent;
            while (parent >= 0) { depth++; parent = rows[parent].Parent; }
            return row.Html ?? string.Concat(Enumerable.Repeat("　", depth)) + string.Concat(row.Parts.Select(p => p.Kind switch
            {
                "image" => used.Single(a => a.Sha256 == p.Sha256).Html,
                "mermaid" => MermaidCards.Html(p.Text ?? ""),
                "latex" => WebUtility.HtmlEncode(AnkiMathText.Normalize((p.Block == true ? "\\[" : "\\(") + p.Text + (p.Block == true ? "\\]" : "\\)"))),
                _ => AnkiNoteDraft.Html(p.Text ?? "")
            }));
        }));
        var backHtml = Html(body) + (notes.Count == 0 ? "" : "<hr><div style=\"text-align:left\"><div>批注：</div>" + Html(notes) + "</div>");
        var frontHtml = Html(frontBody) + (frontNotes.Count == 0 ? "" : "<hr><div style=\"text-align:left\"><div>批注：</div>" + Html(frontNotes) + "</div>");
        return new(frontHtml, backHtml, new("card.0", "basic", frontParts, b.Select(r => r.Parts).ToArray())
            { BackParents = b.Select(r => r.Parent).ToArray() }, used);
    }

    private static List<Row> Parse(string source, IReadOnlyList<ImageAsset> assets)
    {
        var normalized = BracketMath(NormalizeMediaReferences(source, assets).Replace("\r\n", "\n"));
        var document = Markdown.Parse(normalized, Pipeline);
        var rows = new List<Row>();
        int Add(RemNoteRichPart[] parts, int parent, string? html = null)
        {
            if (!parts.Any(p => p.Kind == "image" || !string.IsNullOrWhiteSpace(p.Text))) return parent;
            rows.Add(new(parts, parent, html)); return rows.Count - 1;
        }
        RemNoteRichPart[] Inlines(ContainerInline? inline, bool tableCell = false)
        {
            var parts = new List<RemNoteRichPart>();
            void Walk(Inline? current)
            {
                for (; current != null; current = current.NextSibling)
                    switch (current)
                    {
                        case MathInline math: parts.Add(new("latex", math.Content.ToString(), Block: math.DelimiterCount > 1)); break;
                        case CodeInline code: parts.Add(new("text", tableCell ? code.Content.Replace("\\|", "|") : code.Content)); break;
                        case LiteralInline literal: parts.Add(new("text", literal.Content.ToString())); break;
                        case LineBreakInline: parts.Add(new("text", "\n")); break;
                        case LinkInline { IsImage: true } image:
                            var url = image.Url ?? "";
                            var asset = url.StartsWith("asset:", StringComparison.Ordinal) ? assets.SingleOrDefault(a => a.Sha256 == url[6..]) : null;
                            if (asset == null) throw new InvalidDataException("图片未登记或缺失：" + url);
                            asset.ReadVerified(); parts.Add(new("image", Uri: new Uri(asset.Path).AbsoluteUri, Sha256: asset.Sha256)); break;
                        case ContainerInline container: Walk(container.FirstChild); break;
                        case AutolinkInline link: parts.Add(new("text", link.Url)); break;
                        default: throw new InvalidDataException("暂不能无损导出 Markdown 内联类型：" + current.GetType().Name);
                    }
            }
            Walk(inline?.FirstChild); return Compact(parts);
        }
        void AddTable(Table table, int parent)
        {
            var native = new StringBuilder(@"\def\arraystretch{1.6}\begin{array}{|");
            var html = new StringBuilder("<div style=\"max-width:100%;overflow-x:auto;text-align:left\"><table style=\"border-collapse:collapse;min-width:100%;table-layout:auto\">");
            var images = new List<RemNoteRichPart>();
            foreach (var column in table.ColumnDefinitions)
                native.Append(column.Alignment == TableColumnAlign.Right ? "r|" : column.Alignment == TableColumnAlign.Center ? "c|" : "l|");
            native.Append(@"}\hline ");
            foreach (TableRow row in table)
            {
                html.Append("<tr>");
                for (var index = 0; index < table.ColumnDefinitions.Count; index++)
                {
                    if (index > 0) native.Append(" & ");
                    var tag = row.IsHeader ? "th" : "td";
                    var alignment = table.ColumnDefinitions[index].Alignment;
                    html.Append('<').Append(tag).Append(" style=\"border:1px solid #89939b;padding:8px 12px;min-width:8em;max-width:28em;vertical-align:top;white-space:normal;overflow-wrap:anywhere;text-align:")
                        .Append(alignment == TableColumnAlign.Right ? "right" : alignment == TableColumnAlign.Center ? "center" : "left").Append("\">");
                    var cell = index < row.Count ? (TableCell)row[index] : null;
                    var parts = cell == null ? [] : cell.OfType<LeafBlock>().SelectMany(leaf => Inlines(leaf.Inline, true)).ToArray();
                    var cellImages = parts.Where(p => p.Kind == "image").ToArray();
                    var imageNumber = images.Count;
                    var nativeParts = parts.Select(p => p.Kind == "image"
                        ? new RemNoteRichPart("text", "［图 " + (++imageNumber) + "］") : p).ToArray();
                    native.Append(TableMath.Cell(nativeParts, row.IsHeader));
                    images.AddRange(cellImages);
                    foreach (var leaf in cell?.OfType<LeafBlock>() ?? []) html.Append(TableHtml(leaf.Inline));
                    html.Append("</").Append(tag).Append('>');
                }
                native.Append(@" \\ \hline ");html.Append("</tr>");
            }
            native.Append(@"\end{array}");html.Append("</table></div>");
            // Use RemNote's native formula RichText on both note and card. Anki receives a real HTML grid.
            // Image cells retain numbered positions; their exact registered bytes follow the native grid.
            var rich = new List<RemNoteRichPart> { new("latex", native.ToString(), Block: true) };
            for (var i = 0; i < images.Count; i++) rich.AddRange([new("text", "\n图 " + (i + 1) + "："), images[i]]);
            Add(rich.ToArray(), parent, html.ToString());
        }
        string TableHtml(ContainerInline? inline)
        {
            var html = new StringBuilder();
            void Walk(Inline? current)
            {
                for (; current != null; current = current.NextSibling)
                    switch (current)
                    {
                        case MathInline math:
                            html.Append("<span style=\"display:inline-block;max-width:28em;overflow-x:auto;vertical-align:middle;white-space:nowrap\">")
                                .Append(WebUtility.HtmlEncode(AnkiMathText.Normalize((math.DelimiterCount > 1 ? "\\[" : "\\(") + math.Content + (math.DelimiterCount > 1 ? "\\]" : "\\)"))))
                                .Append("</span>");break;
                        case CodeInline code: html.Append("<code>").Append(WebUtility.HtmlEncode(code.Content.Replace("\\|", "|"))).Append("</code>"); break;
                        case LiteralInline literal: html.Append(AnkiNoteDraft.Html(literal.Content.ToString())); break;
                        case LineBreakInline: html.Append("<br>"); break;
                        case LinkInline { IsImage: true } image:
                            var asset = assets.Single(a => "asset:" + a.Sha256 == image.Url);html.Append(asset.Html);break;
                        case EmphasisInline emphasis:
                            var tag = emphasis.DelimiterCount == 2 ? "strong" : "em";
                            html.Append('<').Append(tag).Append('>');Walk(emphasis.FirstChild);html.Append("</").Append(tag).Append('>');break;
                        case ContainerInline container: Walk(container.FirstChild); break;
                        case AutolinkInline link: html.Append(WebUtility.HtmlEncode(link.Url)); break;
                        default: throw new InvalidDataException("暂不能无损导出表格内联类型：" + current.GetType().Name);
                    }
            }
            Walk(inline?.FirstChild);return html.ToString();
        }
        void Blocks(IEnumerable<Block> container, int inherited)
        {
            var headings = new Stack<(int Level, int Row)>();
            foreach (var block in container)
            {
                var parent = headings.Count == 0 ? inherited : headings.Peek().Row;
                switch (block)
                {
                    case Table table: AddTable(table, parent); break;
                    case HeadingBlock heading:
                        while (headings.Count > 0 && headings.Peek().Level >= heading.Level) headings.Pop();
                        parent = headings.Count == 0 ? inherited : headings.Peek().Row;
                        headings.Push((heading.Level, Add(Inlines(heading.Inline), parent))); break;
                    case ListBlock list:
                        foreach (ListItemBlock item in list)
                        {
                            var itemParent = parent; var initial = true;
                            foreach (var child in item)
                            {
                                if (child is ParagraphBlock paragraph)
                                {
                                    var index = Add(Inlines(paragraph.Inline), itemParent);
                                    if (initial) itemParent = index;
                                }
                                else if (child is Table table) AddTable(table, itemParent);
                                else if (child is ContainerBlock nested)
                                {
                                    if (nested is ListBlock) Blocks(new Block[] { nested }, itemParent);
                                    else Blocks(nested, itemParent);
                                }
                                else if (child is MathBlock math) Add([new("latex", math.Lines.ToString(), Block: true)], itemParent);
                                else if (child is CodeBlock code) Add(CodeParts(code), itemParent);
                                else throw new InvalidDataException("列表中存在尚未支持的块。");
                                initial = false;
                            }
                        }
                        break;
                    case MathBlock math: Add([new("latex", math.Lines.ToString(), Block: true)], parent); break;
                    case CodeBlock code: Add(CodeParts(code), parent); break;
                    case LeafBlock leaf when leaf.Inline != null: Add(Inlines(leaf.Inline), parent); break;
                    case ContainerBlock nested: Blocks(nested, parent); break;
                    case ThematicBreakBlock: break;
                    default: throw new InvalidDataException("暂不能无损导出 Markdown 块：" + block.GetType().Name);
                }
            }
        }
        Blocks(document, -1); return rows;
    }

    private static RemNoteRichPart[] CodeParts(CodeBlock code) => [new(
        code is FencedCodeBlock fenced && string.Equals(fenced.Info?.Trim(), "mermaid", StringComparison.OrdinalIgnoreCase)
            ? "mermaid" : "text", code.Lines.ToString())];

    private static RemNoteRichPart[] Compact(List<RemNoteRichPart> parts)
    {
        var result = new List<RemNoteRichPart>();
        foreach (var part in parts)
            if (part.Kind == "text" && result.LastOrDefault()?.Kind == "text")
                result[^1] = result[^1] with { Text = result[^1].Text + part.Text };
            else result.Add(part);
        return result.ToArray();
    }

    public static string BracketMath(string source) => NormalizeBrackets(source).Text;

    public static ChatCopyMath[] CopyMath(string source)
    {
        if (!source.Contains('$') && !source.Contains("\\(") && !source.Contains("\\[")) return [];
        var normalized = NormalizeBrackets(source);
        return Markdown.Parse(normalized.Text, Pipeline).Descendants().Where(n => n is MathInline or MathBlock)
            .OrderBy(n => n.Span.Start).Select(n =>
            {
                var start = normalized.Map[n.Span.Start];
                var end = normalized.Map[Math.Min(n.Span.End, normalized.Map.Length - 1)] + 1;
                return new ChatCopyMath(n is MathInline inline ? inline.Content.ToString() : ((MathBlock)n).Lines.ToString(),
                    source[start..end], n is MathBlock || n is MathInline { DelimiterCount: > 1 });
            }).ToArray();
    }

    public static string ForDisplay(string source, IReadOnlyList<ImageAsset>? assets = null)
    {
        var text = BracketMath(assets == null ? source : NormalizeMediaReferences(source, assets)); var ast = Markdown.Parse(text, Pipeline);
        var protectedSpans = ast.Descendants().Where(n => n is MathInline or MathBlock or CodeInline or CodeBlock).Select(n => n.Span).ToArray();
        var edits = new List<(int Start, int Length, string Value)>();
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '$' && (i == 0 || text[i - 1] != '\\') && !protectedSpans.Any(s => i >= s.Start && i <= s.End))
                edits.Add((i, 1, "\\$"));
        foreach (var image in ast.Descendants().OfType<LinkInline>().Where(n => n.IsImage && n.Url?.StartsWith("asset:", StringComparison.Ordinal) == true))
        {
            var sha = image.Url![6..];
            if (assets?.Any(a => a.Sha256 == sha) == true)
                edits.Add((image.UrlSpan.Start, image.UrlSpan.Length, "https://clipper-media.local/" + sha + ".png"));
        }
        var result = new StringBuilder(text);
        foreach (var edit in edits.OrderByDescending(e => e.Start)) result.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Value);
        return result.ToString();
    }

    // Parse formulas while preserving every non-math character of legacy card fields.
    internal static RemNoteRichPart[] MathParts(string source)
    {
        var normalized = NormalizeBrackets(source);
        var ast = Markdown.Parse(normalized.Text, Pipeline);
        var formulas = ast.Descendants().Where(n => n is MathInline or MathBlock).OrderBy(n => n.Span.Start);
        var result = new List<RemNoteRichPart>(); var offset = 0;
        foreach (var node in formulas)
        {
            var start = normalized.Map[node.Span.Start];
            var end = normalized.Map[Math.Min(node.Span.End, normalized.Map.Length - 1)] + 1;
            if (start < offset) continue;
            if (start > offset) result.Add(new("text", source[offset..start]));
            var literal = source[start..end];
            var width = literal.StartsWith("\\", StringComparison.Ordinal) || literal.StartsWith("$$", StringComparison.Ordinal) ? 2 : 1;
            result.Add(new("latex", literal[width..^width], Block: node is MathBlock || node is MathInline { DelimiterCount: > 1 }));
            offset = end;
        }
        if (offset < source.Length) result.Add(new("text", source[offset..]));
        return result.ToArray();
    }

    private static (string Text, int[] Map) NormalizeBrackets(string source)
    {
        var ast = Markdown.Parse(source, CodeSpans);
        var protectedSpans = ast.Descendants().Where(n => n is CodeInline or CodeBlock).Select(n => n.Span).ToArray();
        bool Protected(int index) => protectedSpans.Any(s => index >= s.Start && index <= s.End);
        var result = new StringBuilder(); var map = new List<int>();
        void Append(string value, int index) { result.Append(value); map.AddRange(Enumerable.Repeat(index, value.Length)); }
        for (var i = 0; i < source.Length; i++)
        {
            if (Protected(i) || source[i] != '\\' || i + 1 >= source.Length || source[i + 1] is not ('(' or '[') ||
                i > 0 && source[i - 1] == '\\') { Append(source[i].ToString(), i); continue; }
            var block = source[i + 1] == '['; var close = block ? "\\]" : "\\)";
            var end = source.IndexOf(close, i + 2, StringComparison.Ordinal);
            if (end < 0 || Protected(end)) { Append(source[i].ToString(), i); continue; }
            Append(block ? "\n$$\n" : "$", i);
            for (var position = i + 2; position < end; position++) Append(source[position].ToString(), position);
            Append(block ? "\n$$\n" : "$", end + 1);
            i = end + 1;
        }
        return (result.ToString(), map.ToArray());
    }
}
