using Zing.Core;

namespace Zing.Core.Tests;

public sealed class LaunchFilesTests
{
	private const string Pdf = @"C:\Users\A B\Documents\report.pdf";
	private const string Html = @"C:\Users\A B\Documents\page.HTML";

	private static List<LaunchFile> Files(params string[] argv) => LaunchArgs.Files(argv, _ => true);

	[Fact]
	public void Open_with_hands_over_an_existing_pdf_or_html_file()
	{
		Assert.Equal([new LaunchFile(Pdf, LaunchFileKind.Pdf), new LaunchFile(Html, LaunchFileKind.Html)], Files(Pdf, $"\"{Html}\""));
		Assert.Equal([LaunchFileKind.Html], Files(@"D:\site\index.htm").Select(f => f.Kind));
	}

	[Fact]
	public void A_file_that_is_not_there_is_ignored()
	{
		Assert.Empty(LaunchArgs.Files([Pdf], _ => false));
	}

	[Theory]
	[InlineData(@"file:///C:/Users/A%20B/Documents/report.pdf")]
	[InlineData(@"\server\share\report.pdf")]
	[InlineData(@"\?\C:\report.pdf")]
	[InlineData(@"Documents\report.pdf")]
	[InlineData(@"C:report.pdf")]
	[InlineData(@"C:\Windows\win.ini")]
	[InlineData(@"C:\tools\run.exe")]
	[InlineData(@"C:\notes.pdf.exe")]
	[InlineData("--flag=C:\report.pdf")]
	[InlineData("https://a.test/report.pdf")]
	public void Only_a_drive_path_to_a_pdf_or_html_file_counts(string argument)
	{
		Assert.Empty(Files(argument));
	}

	[Fact]
	public void Links_and_files_are_told_apart()
	{
		string[] argv = ["https://a.test/x", Pdf];
		Assert.Equal(["https://a.test/x"], LaunchArgs.Urls(argv));
		Assert.Equal([Pdf], LaunchArgs.Files(argv, _ => true).Select(f => f.Path));
	}

	[Fact]
	public void An_html_file_opens_at_its_file_address()
	{
		Assert.Equal("file:///C:/Users/A%20B/Documents/page.HTML", Files(Html).Single().Address);
	}
}
