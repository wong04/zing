using Zing.Core.Engines;

namespace Zing.Core.Tests;

/// <summary>Convert's HTML to PDF: Chromium prints a page the person picked; this is everything around that call.</summary>
public sealed class HtmlPdfTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("zing-htmlpdf-").FullName;
	private string Downloads => Path.Combine(_root, "downloads");

	public void Dispose() => Directory.Delete(_root, recursive: true);

	private string Page(string name, string html)
	{
		var path = Path.Combine(_root, name);
		File.WriteAllText(path, html);
		return path;
	}

	private static Task Print(string html, string pdf, CancellationToken cancellation)
	{
		File.WriteAllText(pdf, "%PDF-fake " + html.Length);
		return Task.CompletedTask;
	}

	[Fact]
	public async Task A_picked_page_is_printed_into_Downloads_under_the_requested_name()
	{
		var page = Page("page.html", "<h1>Hi</h1>");

		var placed = await HtmlPdf.PrintAsync(page, "page.pdf", Downloads, Print, CancellationToken.None);

		Assert.Equal(Path.Combine(Downloads, "page.pdf"), placed);
		Assert.Equal("%PDF-fake 11", File.ReadAllText(placed));
		Assert.Equal(["page.pdf"], Directory.GetFileSystemEntries(Downloads).Select(Path.GetFileName));
	}

	[Fact]
	public async Task A_name_that_is_taken_is_numbered_and_the_old_file_is_untouched()
	{
		var page = Page("page.html", "<p>x</p>");
		Directory.CreateDirectory(Downloads);
		File.WriteAllText(Path.Combine(Downloads, "page.pdf"), "mine");

		var placed = await HtmlPdf.PrintAsync(page, "page.pdf", Downloads, Print, CancellationToken.None);

		Assert.Equal(Path.Combine(Downloads, "page 2.pdf"), placed);
		Assert.Equal("mine", File.ReadAllText(Path.Combine(Downloads, "page.pdf")));
		Assert.Equal(2, Directory.GetFiles(Downloads).Length);
	}

	[Fact]
	public async Task The_original_page_is_never_changed()
	{
		var page = Page("page.html", "<p>x</p>");
		var before = File.ReadAllBytes(page);

		await HtmlPdf.PrintAsync(page, "page.pdf", Downloads, Print, CancellationToken.None);

		Assert.Equal(before, File.ReadAllBytes(page));
	}

	[Theory]
	[InlineData("page.exe")]
	[InlineData("page.pdf.exe")]
	[InlineData("page")]
	[InlineData("..\\page.pdf")]
	public async Task Only_a_pdf_name_is_accepted(string name)
	{
		var page = Page("page.html", "<p>x</p>");

		await Assert.ThrowsAnyAsync<HtmlPdfException>(() => HtmlPdf.PrintAsync(page, name, Downloads, Print, CancellationToken.None));

		Assert.False(Directory.Exists(Downloads) && Directory.GetFileSystemEntries(Downloads).Length > 0);
	}

	[Theory]
	[InlineData("page.js")]
	[InlineData("page.svg")]
	[InlineData("page.pdf")]
	[InlineData("page")]
	public async Task Only_an_html_file_is_printed(string name)
	{
		var page = Page(name, "<p>x</p>");

		await Assert.ThrowsAsync<NotHtmlException>(() => HtmlPdf.PrintAsync(page, "out.pdf", Downloads, Print, CancellationToken.None));
	}

	[Fact]
	public async Task A_page_too_big_to_hand_to_the_browser_is_refused_by_size()
	{
		var page = Page("big.html", new string('x', HtmlPdf.MaxHtmlBytes + 1));

		var error = await Assert.ThrowsAsync<HtmlTooLargeException>(() => HtmlPdf.PrintAsync(page, "big.pdf", Downloads, Print, CancellationToken.None));

		Assert.Contains("big.html", error.Message);
	}

	[Fact]
	public async Task A_failed_print_leaves_nothing_behind()
	{
		var page = Page("page.html", "<p>x</p>");
		static Task Fails(string html, string pdf, CancellationToken cancellation)
		{
			File.WriteAllText(pdf, "half");
			throw new InvalidOperationException("the page would not print");
		}

		await Assert.ThrowsAsync<InvalidOperationException>(() => HtmlPdf.PrintAsync(page, "page.pdf", Downloads, Fails, CancellationToken.None));

		Assert.Empty(Directory.GetFileSystemEntries(Downloads));
	}

	[Fact]
	public async Task A_cancel_leaves_no_output_even_when_the_print_finishes()
	{
		var page = Page("page.html", "<p>x</p>");
		using var cancel = new CancellationTokenSource();
		Task CancelsDuring(string html, string pdf, CancellationToken cancellation)
		{
			File.WriteAllText(pdf, "%PDF-fake");
			cancel.Cancel();
			return Task.CompletedTask;
		}

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HtmlPdf.PrintAsync(page, "page.pdf", Downloads, CancelsDuring, cancel.Token));

		Assert.Empty(Directory.GetFileSystemEntries(Downloads));
	}

	[Fact]
	public async Task A_print_that_wrote_no_file_is_an_error_not_a_success()
	{
		var page = Page("page.html", "<p>x</p>");

		await Assert.ThrowsAsync<HtmlPdfException>(() => HtmlPdf.PrintAsync(page, "page.pdf", Downloads, (_, _, _) => Task.CompletedTask, CancellationToken.None));
	}

	[Fact]
	public async Task The_browser_is_handed_the_page_text_not_its_path()
	{
		var page = Page("page.html", "<p>café</p>");
		string? seen = null;

		await HtmlPdf.PrintAsync(page, "page.pdf", Downloads, (html, pdf, _) =>
		{
			seen = html;
			File.WriteAllText(pdf, "%PDF");
			return Task.CompletedTask;
		}, CancellationToken.None);

		Assert.Equal("<p>café</p>", seen);
	}
}
