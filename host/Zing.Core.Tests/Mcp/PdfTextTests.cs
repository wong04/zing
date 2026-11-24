using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Zing.Core.Mcp;

namespace Zing.Core.Tests.Mcp;

public sealed class PdfTextTests
{
	private static byte[] Pdf(params string[][] pages)
	{
		var builder = new PdfDocumentBuilder();
		var font = builder.AddStandard14Font(Standard14Font.Helvetica);
		foreach (var lines in pages)
		{
			var page = builder.AddPage(400, 300);
			for (var i = 0; i < lines.Length; i++) page.AddText(lines[i], 12, new UglyToad.PdfPig.Core.PdfPoint(20, 260 - i * 20), font);
		}
		return builder.Build();
	}

	[Fact]
	public void ReadsEveryPageInOrderWithLinesKept()
	{
		var text = PdfText.From(Pdf(["Quarterly Zebra Report", "Second line"], ["Page two"]));

		Assert.Equal("Quarterly Zebra Report\nSecond line\n\nPage two", text);
	}

	[Fact]
	public void APdfWithNoTextIsEmpty() => Assert.Equal("", PdfText.From(Pdf([])));

	[Fact]
	public void BytesThatArentAPdfAreUnsupported()
	{
		var error = Assert.Throws<AiToolException>(() => PdfText.From("<!doctype html><p>not a pdf"u8.ToArray()));
		Assert.Equal(AiErrors.Unsupported, error.Code);
	}
}
