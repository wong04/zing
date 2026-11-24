using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Zing.Core.Mcp;

/// <summary>
/// read_text of a PDF tab: the text of the PDF's own bytes, read here rather than through the viewer, whose scripting reads
/// only a selection it leaves behind in the owner's view. Pages are separated by a blank line.
/// </summary>
public static class PdfText
{
	public static string From(byte[] pdf)
	{
		PdfDocument document;
		try
		{
			document = PdfDocument.Open(pdf);
		}
		catch (PdfDocumentFormatException error)
		{
			throw new AiToolException(AiErrors.Unsupported, $"The tab's file isn't a PDF Zing can read: {error.Message}");
		}
		using (document)
			return string.Join("\n\n", document.GetPages().Select(PageText)).Trim();
	}

	private static string PageText(Page page) => ContentOrderTextExtractor.GetText(page).ReplaceLineEndings("\n").Trim();
}
