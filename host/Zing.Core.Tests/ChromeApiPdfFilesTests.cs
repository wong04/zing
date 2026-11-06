using System.Text;
using System.Text.Json;
using Zing.Core.Engines;

namespace Zing.Core.Tests;

/// <summary>files:read and files:save-bytes: the PDF tools' way to get a granted file's bytes and to put a result in Downloads.</summary>
public sealed class ChromeApiPdfFilesTests : IDisposable
{
	private readonly Bridge _bridge = new();
	private readonly FakeWindowHost _host = new();
	private readonly string _root = Path.Combine(Path.GetTempPath(), "zing-pdf-files-" + Guid.NewGuid().ToString("N"));
	private readonly string _downloads;
	private readonly ChromeApi _api;

	private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n%fake\n");
	private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0];
	private static readonly byte[] Jpg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0];

	public ChromeApiPdfFilesTests()
	{
		_downloads = Path.Combine(_root, "downloads");
		Directory.CreateDirectory(_root);
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		_api = new ChromeApi(_bridge, new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest()), _host, "test");
		_host.DownloadsDirectory = _downloads;
		_api.Start([], "about:blank");
	}

	public void Dispose()
	{
		_api.Dispose();
		if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
	}

	private async Task<JsonElement> Invoke(string channel, params object?[] args)
	{
		var json = JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel, args });
		return JsonDocument.Parse(await _bridge.HandleAsync(json)).RootElement;
	}

	private static string ErrorName(JsonElement result) => result.GetProperty("error").GetProperty("name").GetString()!;

	private async Task<string> Grant(string name, byte[] bytes)
	{
		var path = Path.Combine(_root, name);
		File.WriteAllBytes(path, bytes);
		_host.PickedPaths = [path];
		return (await Invoke("files:pick", (object)new[] { "any" })).GetProperty("value")[0].GetProperty("token").GetString()!;
	}

	private string[] Saved() => Directory.Exists(_downloads) ? [.. Directory.GetFileSystemEntries(_downloads).Select(Path.GetFileName).Cast<string>().Order(StringComparer.Ordinal)] : [];

	private static string B64(byte[] bytes) => Convert.ToBase64String(bytes);

	[Fact]
	public async Task A_granted_pdf_png_and_jpeg_are_read_as_their_bytes()
	{
		foreach (var (name, bytes) in new[] { ("a.pdf", Pdf), ("b.PNG", Png), ("c.jpg", Jpg), ("d.jpeg", Jpg) })
		{
			var read = await Invoke("files:read", await Grant(name, bytes));
			Assert.True(read.GetProperty("ok").GetBoolean(), name);
			Assert.Equal(bytes, Convert.FromBase64String(read.GetProperty("value").GetString()!));
		}
	}

	[Fact]
	public async Task A_path_the_renderer_made_up_is_not_read()
	{
		Assert.Equal(nameof(UnknownFileGrantException), ErrorName(await Invoke("files:read", @"C:\Windows\win.ini")));
		Assert.Equal(nameof(UnknownFileGrantException), ErrorName(await Invoke("files:read", "")));
	}

	[Fact]
	public async Task A_granted_file_of_another_kind_is_not_read()
	{
		foreach (var name in new[] { "notes.txt", "run.exe", "secrets.docx", "image.svg", "noext" })
		{
			Assert.Equal(nameof(PdfFileRefusedException), ErrorName(await Invoke("files:read", await Grant(name, Pdf))));
		}
	}

	[Fact]
	public async Task A_file_whose_content_is_not_what_its_name_says_is_not_read()
	{
		Assert.Equal(nameof(PdfFileRefusedException), ErrorName(await Invoke("files:read", await Grant("fake.pdf", Encoding.ASCII.GetBytes("MZ not a pdf")))));
		Assert.Equal(nameof(PdfFileRefusedException), ErrorName(await Invoke("files:read", await Grant("fake.png", Jpg))));
		Assert.Equal(nameof(PdfFileRefusedException), ErrorName(await Invoke("files:read", await Grant("fake.jpg", Pdf))));
	}

	[Fact]
	public async Task A_file_over_the_read_limit_is_not_read()
	{
		var token = await Grant("big.pdf", Pdf);
		using (var stream = new FileStream(Path.Combine(_root, "big.pdf"), FileMode.Open)) stream.SetLength(PdfFiles.MaxReadBytes + 1);
		var refused = await Invoke("files:read", token);
		Assert.Equal(nameof(PdfFileRefusedException), ErrorName(refused));
		Assert.Contains("50 MB", refused.GetProperty("error").GetProperty("message").GetString());
	}

	[Fact]
	public async Task Saved_bytes_land_in_downloads_under_a_numbered_name_and_answer_with_a_name_and_token_not_a_path()
	{
		var first = await Invoke("files:save-bytes", "merged.pdf", B64(Pdf));
		var second = await Invoke("files:save-bytes", "merged.pdf", B64(Pdf));

		Assert.Equal("merged.pdf", first.GetProperty("value").GetProperty("name").GetString());
		Assert.Equal("merged 2.pdf", second.GetProperty("value").GetProperty("name").GetString());
		Assert.Matches("^[0-9a-f]{32}$", second.GetProperty("value").GetProperty("token").GetString()!);
		Assert.DoesNotContain(_root.Replace("\\", "\\\\"), second.ToString());
		Assert.Equal(["merged 2.pdf", "merged.pdf"], Saved());
		Assert.Equal(Pdf, File.ReadAllBytes(Path.Combine(_downloads, "merged.pdf")));
	}

	[Fact]
	public async Task A_saved_file_is_never_written_over_an_existing_one()
	{
		Directory.CreateDirectory(_downloads);
		File.WriteAllText(Path.Combine(_downloads, "mine.pdf"), "mine");
		await Invoke("files:save-bytes", "mine.pdf", B64(Pdf));
		Assert.Equal("mine", File.ReadAllText(Path.Combine(_downloads, "mine.pdf")));
		Assert.Equal(["mine 2.pdf", "mine.pdf"], Saved());
	}

	[Fact]
	public async Task Png_and_jpeg_pages_are_saved_too_and_the_saved_token_can_be_read_back()
	{
		var png = await Invoke("files:save-bytes", "book-p001.png", B64(Png));
		Assert.True((await Invoke("files:save-bytes", "book-p002.jpg", B64(Jpg))).GetProperty("ok").GetBoolean());
		var back = await Invoke("files:read", png.GetProperty("value").GetProperty("token").GetString());
		Assert.Equal(Png, Convert.FromBase64String(back.GetProperty("value").GetString()!));
	}

	[Fact]
	public async Task Only_a_plain_name_ending_in_pdf_png_or_jpg_is_accepted()
	{
		var bad = new[]
		{
			@"..\evil.pdf", "../evil.pdf", @"C:\x.pdf", "sub/x.pdf", "tool.exe", "tool.pdf.exe", "tool.bat", "tool", ".pdf", "a%d.pdf", "a[0].png", "a:b.pdf",
			"con.pdf", "bad\u202egpj.pdf", "ctl\u0001.pdf", "", new string('a', 101) + ".pdf", "x.PDF.", "x.jpeg",
		};
		foreach (var name in bad)
		{
			var result = await Invoke("files:save-bytes", name, B64(Pdf));
			Assert.False(result.GetProperty("ok").GetBoolean(), $"accepted {name}");
			Assert.Equal(nameof(PdfFileRefusedException), ErrorName(result));
		}
		Assert.Empty(Saved());
	}

	[Fact]
	public async Task Bytes_that_are_not_the_kind_the_name_says_are_not_saved()
	{
		Assert.Equal(nameof(PdfFileRefusedException), ErrorName(await Invoke("files:save-bytes", "x.pdf", B64(Encoding.ASCII.GetBytes("MZ\u0090 program")))));
		Assert.Equal(nameof(PdfFileRefusedException), ErrorName(await Invoke("files:save-bytes", "x.png", B64(Pdf))));
		Assert.Equal(nameof(PdfFileRefusedException), ErrorName(await Invoke("files:save-bytes", "x.pdf", B64([]))));
		Assert.Equal(nameof(FormatException), ErrorName(await Invoke("files:save-bytes", "x.pdf", "not base64!!")));
		Assert.Empty(Saved());
	}

	[Fact]
	public void More_than_the_save_limit_is_refused_before_anything_is_written()
	{
		var big = new byte[PdfFiles.MaxSaveBytes + 1];
		Pdf.CopyTo(big, 0);
		var error = Assert.Throws<PdfFileRefusedException>(() => PdfFiles.Save(_downloads, "big.pdf", big));
		Assert.Contains("100 MB", error.Message);
		Assert.False(Directory.Exists(_downloads) && Directory.GetFileSystemEntries(_downloads).Length > 0);
	}

	[Fact]
	public async Task A_huge_base64_text_is_refused_without_decoding_it()
	{
		var result = await Invoke("files:save-bytes", "big.pdf", new string('A', (int)(PdfFiles.MaxSaveBytes / 3 * 4) + 8));
		Assert.Equal(nameof(PdfFileRefusedException), ErrorName(result));
	}
}
