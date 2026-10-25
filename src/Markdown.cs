using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DrangedAddons;

/// <summary>
/// Renders the Markdown in READMEs and changelogs (GitHub flavour, the common parts) as WPF elements: headings,
/// paragraphs, lists, quotes, code, tables, rules, images, links, bold / italic / code / strikethrough. HTML that
/// READMEs often use (img, a, br, b, i, h1–h6) is converted first; other tags are dropped.
/// </summary>
public sealed partial class MarkdownRenderer(
    Func<string, string> resolveLink,
    Func<string, string> resolveImage,
    Func<string, Task<ImageSource?>> loadImage)
{
    const double BodySize = 13.5;

    static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public void Render(Panel target, string markdown)
    {
        foreach (var block in Blocks(Normalise(markdown)))
            target.Children.Add(block);
    }

    // ---- HTML → Markdown ------------------------------------------------------------------------------------------

    static string Normalise(string text)
    {
        text = text.Replace("\r", "");
        text = HtmlComment().Replace(text, "");
        text = HtmlImg().Replace(text, m => $"![{Attr(m.Value, "alt")}]({Attr(m.Value, "src")})");
        text = HtmlLink().Replace(text, m => $"[{m.Groups[2].Value.Trim()}]({m.Groups[1].Value})");
        text = HtmlHeading().Replace(text, m => $"\n{new string('#', int.Parse(m.Groups[1].Value))} {m.Groups[2].Value.Trim()}\n");
        text = HtmlBreak().Replace(text, "  \n");
        text = HtmlBold().Replace(text, "**");
        text = HtmlItalic().Replace(text, "*");
        text = HtmlBlock().Replace(text, "\n");
        text = HtmlAnyTag().Replace(text, "");
        return System.Net.WebUtility.HtmlDecode(text);
    }

    static string Attr(string tag, string name) =>
        Regex.Match(tag, $"""{name}\s*=\s*["']([^"']*)["']""", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : "";

    // ---- Blocks ---------------------------------------------------------------------------------------------------

    List<UIElement> Blocks(string text)
    {
        var lines = text.Split('\n');
        var blocks = new List<UIElement>();
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            var joined = string.Join("\n", paragraph);
            paragraph.Clear();
            if (ImagesOnly().IsMatch(joined))
                blocks.Add(ImageBlock(joined));
            else
                blocks.Add(TextBlockFor(joined, BodySize, FontWeights.Normal, new Thickness(0, 0, 0, 10)));
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var next = i + 1 < lines.Length ? lines[i + 1] : null;

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            if (line.TrimStart().StartsWith("```") || line.TrimStart().StartsWith("~~~"))
            {
                FlushParagraph();
                var fence = line.TrimStart()[..3];
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith(fence); i++)
                    code.Add(lines[i]);
                blocks.Add(CodeBlock(string.Join("\n", code)));
                continue;
            }

            if (Heading().Match(line) is { Success: true } h)
            {
                FlushParagraph();
                blocks.Add(HeadingBlock(h.Groups[2].Value, h.Groups[1].Length));
                continue;
            }

            if (paragraph.Count == 0 && next is not null && !IsListItem(line) && SetextUnderline().Match(next) is { Success: true } u)
            {
                blocks.Add(HeadingBlock(line.Trim(), u.Value.TrimStart()[0] == '=' ? 1 : 2));
                i++;
                continue;
            }

            if (Rule().IsMatch(line))
            {
                FlushParagraph();
                blocks.Add(new Border { Height = 1, Background = Res("LineBrush"), Margin = new Thickness(0, 6, 0, 14) });
                continue;
            }

            if (line.TrimStart().StartsWith('>'))
            {
                FlushParagraph();
                var quoted = new List<string>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('>'); i++)
                    quoted.Add(QuotePrefix().Replace(lines[i], ""));
                i--;
                blocks.Add(QuoteBlock(string.Join("\n", quoted)));
                continue;
            }

            if (IsListItem(line))
            {
                FlushParagraph();
                var items = new List<string>();
                for (; i < lines.Length; i++)
                {
                    if (IsListItem(lines[i])) items.Add(lines[i]);
                    else if (!string.IsNullOrWhiteSpace(lines[i]) && lines[i].StartsWith("  ") && items.Count > 0)
                        items[^1] += " " + lines[i].Trim();
                    else break;
                }
                i--;
                blocks.Add(ListBlock(items));
                continue;
            }

            if (line.Contains('|') && next is not null && TableDivider().IsMatch(next))
            {
                FlushParagraph();
                var rows = new List<string> { line };
                for (i += 2; i < lines.Length && lines[i].Contains('|') && !string.IsNullOrWhiteSpace(lines[i]); i++)
                    rows.Add(lines[i]);
                i--;
                blocks.Add(TableBlock(rows));
                continue;
            }

            paragraph.Add(line);
        }
        FlushParagraph();
        return blocks;
    }

    static bool IsListItem(string line) => ListItem().IsMatch(line);

    UIElement HeadingBlock(string text, int level)
    {
        double[] sizes = [22, 18.5, 16, 14.5, 13.5, 13];
        var block = TextBlockFor(text, sizes[Math.Clamp(level, 1, 6) - 1], FontWeights.SemiBold,
            new Thickness(0, level <= 2 ? 10 : 6, 0, level <= 2 ? 8 : 6));
        if (level > 1) return block;
        // Top-level headings get a rule underneath, like GitHub.
        var panel = new StackPanel();
        panel.Children.Add(block);
        panel.Children.Add(new Border { Height = 1, Background = Res("LineBrush"), Margin = new Thickness(0, -2, 0, 10) });
        return panel;
    }

    UIElement CodeBlock(string code) => new Border
    {
        Background = Res("Panel2Brush"),
        BorderBrush = Res("LineBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(12, 8, 12, 8),
        Margin = new Thickness(0, 0, 0, 12),
        Child = new TextBlock { Text = code, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap },
    };

    UIElement QuoteBlock(string text)
    {
        var inner = new StackPanel();
        foreach (var block in Blocks(text)) inner.Children.Add(block);
        return new Border
        {
            BorderBrush = Res("AccentBrush"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 2, 0, 0),
            Margin = new Thickness(0, 0, 0, 10),
            Opacity = 0.85,
            Child = inner,
        };
    }

    UIElement ListBlock(List<string> items)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var number = 0;
        foreach (var item in items)
        {
            var m = ListItem().Match(item);
            var indent = m.Groups[1].Value.Replace("\t", "    ").Length / 2;
            var ordered = char.IsDigit(m.Groups[2].Value[0]);
            var text = m.Groups[3].Value;
            if (ordered) number++;
            var marker = ordered ? $"{number}." : "•";
            if (TaskBox().Match(text) is { Success: true } task)
            {
                marker = task.Groups[1].Value.Trim().Length > 0 ? "☑" : "☐";
                text = text[task.Length..];
            }
            var row = new DockPanel { Margin = new Thickness(indent * 18, 0, 0, 3) };
            var bullet = new TextBlock
            {
                Text = marker, Width = ordered ? 22 : 16, FontSize = BodySize,
                Foreground = Res(ordered ? "MutedBrush" : "AccentBrush"),
            };
            DockPanel.SetDock(bullet, Dock.Left);
            row.Children.Add(bullet);
            row.Children.Add(TextBlockFor(text, BodySize, FontWeights.Normal, new Thickness(0)));
            panel.Children.Add(row);
        }
        return panel;
    }

    UIElement TableBlock(List<string> rows)
    {
        var cells = rows.Select(r => r.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList()).ToList();
        var columns = cells.Max(c => c.Count);
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < cells.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < cells[r].Count; c++)
            {
                var cell = new Border
                {
                    BorderBrush = Res("LineBrush"),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Background = r == 0 ? Res("Panel2Brush") : Brushes.Transparent,
                    Padding = new Thickness(10, 5, 14, 5),
                    Child = TextBlockFor(cells[r][c], 13, r == 0 ? FontWeights.SemiBold : FontWeights.Normal, new Thickness(0)),
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        return grid;
    }

    /// <summary>A paragraph that is only images (screenshots, banners): shown full size, wrapping.</summary>
    UIElement ImageBlock(string text)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (Match m in AnyImage().Matches(text))
        {
            var image = new Image { Stretch = Stretch.Uniform, MaxHeight = 520, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 8, 8) };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            panel.Children.Add(image);
            _ = LoadInto(image, resolveImage(m.Groups["src"].Value), fullSize: true);
            if (m.Groups["href"].Success)
            {
                var href = resolveLink(m.Groups["href"].Value);
                image.Cursor = System.Windows.Input.Cursors.Hand;
                image.MouseLeftButtonUp += (_, _) => Open(href);
            }
        }
        return panel;
    }

    async Task LoadInto(Image image, string url, bool fullSize)
    {
        var source = await loadImage(url);
        if (source is null)
        {
            image.Visibility = Visibility.Collapsed;
            return;
        }
        image.Source = source;
        if (fullSize && source is System.Windows.Media.Imaging.BitmapSource bitmap)
            image.MaxWidth = bitmap.PixelWidth; // never upscale past the real size
    }

    // ---- Inline text ----------------------------------------------------------------------------------------------

    TextBlock TextBlockFor(string text, double size, FontWeight weight, Thickness margin)
    {
        var block = new TextBlock { FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, Margin = margin, LineHeight = size * 1.45 };
        AddInlines(block.Inlines, text);
        return block;
    }

    void AddInlines(InlineCollection inlines, string text)
    {
        var at = 0;
        while (at < text.Length)
        {
            var m = InlineToken().Match(text, at);
            if (!m.Success)
            {
                AddPlain(inlines, text[at..]);
                break;
            }
            if (m.Index > at) AddPlain(inlines, text[at..m.Index]);
            at = m.Index + m.Length;

            if (m.Groups["code"].Success)
                inlines.Add(new Run(m.Groups["code"].Value)
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    Background = Res("Panel2Brush"),
                    FontSize = 12.5,
                });
            else if (m.Groups["img"].Success)
                inlines.Add(InlineImage(m.Groups["imgsrc"].Value));
            else if (m.Groups["link"].Success)
            {
                var link = LinkTo(m.Groups["href"].Value);
                AddInlines(link.Inlines, m.Groups["text"].Value);
                inlines.Add(link);
            }
            else if (m.Groups["url"].Success)
            {
                var link = LinkTo(m.Groups["url"].Value);
                link.Inlines.Add(new Run(m.Groups["url"].Value));
                inlines.Add(link);
            }
            else if (m.Groups["bold"].Success)
            {
                var bold = new Bold();
                AddInlines(bold.Inlines, m.Groups["bold"].Value);
                inlines.Add(bold);
            }
            else if (m.Groups["strike"].Success)
            {
                var span = new Span { TextDecorations = TextDecorations.Strikethrough };
                AddInlines(span.Inlines, m.Groups["strike"].Value);
                inlines.Add(span);
            }
            else if (m.Groups["italic"].Success)
            {
                var italic = new Italic();
                AddInlines(italic.Inlines, m.Groups["italic"].Value);
                inlines.Add(italic);
            }
        }
    }

    static void AddPlain(InlineCollection inlines, string text)
    {
        // A line ending in two spaces or a backslash is a hard break; other newlines are spaces.
        var parts = HardBreak().Split(text);
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) inlines.Add(new LineBreak());
            var plain = Escaped().Replace(parts[i].Replace('\n', ' '), "$1");
            if (plain.Length > 0) inlines.Add(new Run(plain));
        }
    }

    Hyperlink LinkTo(string href)
    {
        var url = resolveLink(href);
        var link = new Hyperlink { Foreground = Res("AccentBrush"), TextDecorations = null, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = url };
        link.Click += (_, _) => Open(url);
        return link;
    }

    /// <summary>A small image in a line of text (badges, emoji images): kept to text height.</summary>
    Inline InlineImage(string src)
    {
        var image = new Image { MaxHeight = 22, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 4, -4) };
        _ = LoadInto(image, resolveImage(src), fullSize: false);
        return new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center };
    }

    static void Open(string url)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>Every image URL in some Markdown (README pictures for the gallery), in order.</summary>
    public static IEnumerable<string> ImageSources(string markdown) =>
        AnyImage().Matches(Normalise(markdown)).Select(m => m.Groups["src"].Value);

    // ---- Patterns -------------------------------------------------------------------------------------------------

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)] private static partial Regex HtmlComment();
    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex HtmlImg();
    [GeneratedRegex(@"<a\b[^>]*?href\s*=\s*[""']([^""']*)[""'][^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex HtmlLink();
    [GeneratedRegex(@"<h([1-6])\b[^>]*>(.*?)</h\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex HtmlHeading();
    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)] private static partial Regex HtmlBreak();
    [GeneratedRegex(@"</?(b|strong)>", RegexOptions.IgnoreCase)] private static partial Regex HtmlBold();
    [GeneratedRegex(@"</?(i|em)>", RegexOptions.IgnoreCase)] private static partial Regex HtmlItalic();
    [GeneratedRegex(@"</?(p|div|center|details|summary|table|tr|ul|ol|li)\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex HtmlBlock();
    [GeneratedRegex(@"</?[a-zA-Z][^>]*>")] private static partial Regex HtmlAnyTag();

    [GeneratedRegex(@"^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$")] private static partial Regex Heading();
    [GeneratedRegex(@"^\s{0,3}(=+|-{2,})\s*$")] private static partial Regex SetextUnderline();
    [GeneratedRegex(@"^\s{0,3}((\*\s*){3,}|(-\s*){3,}|(_\s*){3,})$")] private static partial Regex Rule();
    [GeneratedRegex(@"^\s*>\s?")] private static partial Regex QuotePrefix();
    [GeneratedRegex(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$")] private static partial Regex ListItem();
    [GeneratedRegex(@"^\[( |x|X)\]\s+")] private static partial Regex TaskBox();
    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")] private static partial Regex TableDivider();

    /// <summary>An image, or a linked image ([![alt](img)](href)).</summary>
    [GeneratedRegex(@"\[!\[[^\]]*\]\(\s*<?(?<src>[^)\s>]+)>?(?:\s+""[^""]*"")?\s*\)\]\(\s*<?(?<href>[^)\s>]+)>?\s*\)|!\[[^\]]*\]\(\s*<?(?<src>[^)\s>]+)>?(?:\s+""[^""]*"")?\s*\)")]
    private static partial Regex AnyImage();

    [GeneratedRegex(@"^\s*((\[!\[[^\]]*\]\([^)]+\)\]\([^)]+\)|!\[[^\]]*\]\([^)]+\))\s*)+$")] private static partial Regex ImagesOnly();

    [GeneratedRegex(
        @"`(?<code>[^`]+)`" +
        @"|(?<img>!\[[^\]]*\]\(\s*<?(?<imgsrc>[^)\s>]+)>?(?:\s+""[^""]*"")?\s*\))" +
        @"|(?<link>\[(?<text>(?:[^\[\]]|\[[^\]]*\])*)\]\(\s*<?(?<href>[^)\s>]+)>?(?:\s+""[^""]*"")?\s*\))" +
        @"|<(?<url>https?://[^>\s]+)>" +
        @"|(?<url>(?<![(\[""'=])\bhttps?://[^\s<>()]*[^\s<>().,;:!?'""])" +
        @"|\*\*(?<bold>.+?)\*\*|__(?<bold>.+?)__" +
        @"|~~(?<strike>.+?)~~" +
        @"|(?<![*\w])\*(?![\s*])(?<italic>.+?)(?<![\s*])\*(?!\*)|(?<![_\w])_(?![\s_])(?<italic>.+?)(?<![\s_])_(?![_\w])",
        RegexOptions.Singleline)]
    private static partial Regex InlineToken();

    [GeneratedRegex(@"(?: {2,}|\\)\n")] private static partial Regex HardBreak();
    [GeneratedRegex(@"\\([\\`*_{}\[\]()#+\-.!|>~])")] private static partial Regex Escaped();
}
