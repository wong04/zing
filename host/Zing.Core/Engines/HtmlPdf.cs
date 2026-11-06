namespace Zing.Core.Engines;

public class HtmlPdfException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class NotHtmlException(string name) : HtmlPdfException($"{name} isn't an HTML file, so it can't be printed to PDF");

public sealed class HtmlTooLargeException(string name, long bytes)
	: HtmlPdfException($"{name} is {bytes / 1024} KB; Zing can print an HTML file up to {HtmlPdf.MaxHtmlBytes / 1024} KB to PDF, because the browser takes the page as one string");

/// <summary>
/// Convert's HTML to PDF: the page a person picked is printed by Chromium (<c>PrintToPdfAsync</c>) and the PDF is put in
/// Downloads under a name that isn't taken, so the original is never changed and no file is overwritten. What Chromium is
/// allowed to do while printing (no scripts, no network) is set where it runs, in Zing.Host.
/// </summary>
public static class HtmlPdf
{
	/// <summary>
	/// WebView2's NavigateToString takes up to 2 MB of text. The page is given to the browser as that text rather than as
	/// a path, so it can't read files beside it; a bigger page is refused, never cut.
	/// </summary>
	public const int MaxHtmlBytes = 1_500_000;

	private static readonly string[] HtmlExtensions = [".html", ".htm", ".xhtml"];

	/// <summary>Prints <paramref name="htmlPath"/> and returns where the PDF now is.</summary>
	/// <param name="outputName">The wanted name, such as "page.pdf"; numbered if Downloads has one, unless "Never overwrite files" is off.</param>
	/// <param name="print">Renders the page text to the given PDF path (Chromium; a fake in tests).</param>
	/// <exception cref="NotHtmlException">The file isn't an HTML file.</exception>
	/// <exception cref="HtmlTooLargeException">The page is more than <see cref="MaxHtmlBytes"/>.</exception>
	/// <exception cref="HtmlPdfException">The output name isn't a plain .pdf name, or the print made no file.</exception>
	public static async Task<string> PrintAsync(string htmlPath, string outputName, string downloads, OutputPlacement placement, Func<string, string, CancellationToken, Task> print, CancellationToken cancellation)
	{
		var name = Path.GetFileName(htmlPath);
		if (!HtmlExtensions.Contains(Path.GetExtension(htmlPath), StringComparer.OrdinalIgnoreCase)) throw new NotHtmlException(name);
		if (outputName != Path.GetFileName(outputName) || !outputName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || outputName.Length <= 4)
			throw new HtmlPdfException($"\"{outputName}\" isn't a plain name ending in .pdf");
		var size = new FileInfo(htmlPath).Length;
		if (size > MaxHtmlBytes) throw new HtmlTooLargeException(name, size);
		var html = await File.ReadAllTextAsync(htmlPath, cancellation);

		Directory.CreateDirectory(downloads);
		var staged = Path.Combine(downloads, $".{Guid.NewGuid():N}.zing-part");
		try
		{
			await print(html, staged, cancellation);
			// A print that finishes after Cancel must not leave its file.
			cancellation.ThrowIfCancellationRequested();
			if (!File.Exists(staged)) throw new HtmlPdfException($"Printing {name} made no PDF");
			return placement.Place(staged, downloads, outputName);
		}
		finally
		{
			File.Delete(staged);
		}
	}
}
