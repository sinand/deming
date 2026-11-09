using Deming.Server.Panel;

namespace Deming.Tests;

public class MarkdownViewTests
{
    private static string Html(string markdown) => MarkdownView.Render(markdown).Value;

    [Fact]
    public void Common_markdown_is_rendered()
    {
        var html = Html("# Title\n\n- one\n- two\n\n```\ncode <b>\n```\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n[site](https://example.com/x)");

        Assert.Contains("<h1>Title</h1>", html);
        Assert.Contains("<li>two</li>", html);
        Assert.Contains("<pre><code>code &lt;b&gt;", html);
        Assert.Contains("<table>", html);
        Assert.Contains("href=\"https://example.com/x\"", html);
        Assert.Contains("rel=\"nofollow noopener noreferrer\"", html);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>", "<script")]
    [InlineData("<img src=x onerror=alert(1)>", "<img")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>", "<a ")]
    [InlineData("<iframe src=\"https://evil\"></iframe>", "<iframe")]
    [InlineData("text <b onmouseover=alert(1)>bold</b>", "<b ")]
    [InlineData("<div>\n<script>alert(1)</script>\n</div>", "<div")]
    public void Raw_html_stays_escaped(string markdown, string forbidden)
    {
        var html = Html(markdown);

        Assert.DoesNotContain(forbidden, html);
        Assert.Contains("&lt;", html);
    }

    [Theory]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("[x](JaVaScRiPt:alert(1))")]
    [InlineData("[x](data:text/html;base64,PHNjcmlwdD4=)")]
    [InlineData("[x](vbscript:msgbox(1))")]
    [InlineData("[x](file:///etc/passwd)")]
    [InlineData("[x](/relative/path)")]
    [InlineData("[x](//evil.example/x)")]
    [InlineData("[x](#anchor)")]
    [InlineData("<javascript:alert(1)>")]
    [InlineData("[x]: javascript:alert(1)\n\n[x]")]
    public void Links_other_than_http_and_https_are_plain_text(string markdown)
    {
        var html = Html(markdown);

        Assert.DoesNotContain("<a ", html);
        Assert.DoesNotContain("href=", html);
    }

    [Fact]
    public void Images_are_not_rendered()
    {
        var html = Html("![alt text](https://example.com/a.png)");

        Assert.DoesNotContain("<img", html);
        Assert.Contains("alt text", html);
    }

    [Fact]
    public void Http_autolinks_stay_links()
    {
        Assert.Contains("href=\"http://example.com/\"", Html("<http://example.com/>"));
    }

    [Fact]
    public void Blank_input_renders_nothing()
    {
        Assert.Equal("", Html("  "));
    }
}
