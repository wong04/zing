using System.Net;
using Zing.Core;
using Zing.Core.Net;

namespace Zing.Core.Tests;

public sealed class PageImageDropsTests : IDisposable
{
	private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2025-07-29T14:40:00Z");
	private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3];
	private static readonly byte[] Jpeg = [0xff, 0xd8, 0xff, 0xe0, 1, 2, 3];

	private readonly string _root = Path.Combine(Path.GetTempPath(), "zing-page-images-" + Guid.NewGuid().ToString("N"));
	private readonly List<(string What, Exception Error)> _errors = [];
	private readonly StubHandler _handler = new();
	private readonly PageImageDrops _drops;

	public PageImageDropsTests()
	{
		_drops = new PageImageDrops(_root, new HttpClient(_handler), (what, error) => _errors.Add((what, error)));
	}

	public void Dispose()
	{
		_drops.Dispose();
		if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
	}

	private sealed class StubHandler : HttpMessageHandler
	{
		public Func<HttpRequestMessage, HttpResponseMessage> Reply { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

		public List<HttpRequestMessage> Requests { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests.Add(request);
			return Task.FromResult(Reply(request));
		}
	}

	private static HttpResponseMessage Bytes(byte[] body, string type = "image/png") =>
		new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) { Headers = { ContentType = new(type) } } };

	private Task<string> Fetch(string address) => _drops.FetchAsync(address, Now, TestContext.Current.CancellationToken);

	[Fact]
	public async Task An_http_image_is_fetched_into_a_folder_of_this_process_under_its_own_name()
	{
		_handler.Reply = _ => Bytes(Png);
		var path = await Fetch("https://example.com/pics/cat.png?size=2");
		Assert.Equal("cat.png", Path.GetFileName(path));
		Assert.Equal(Png, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
		Assert.Equal(_root, Path.GetDirectoryName(Path.GetDirectoryName(path)));
		Assert.StartsWith($"{Environment.ProcessId}_", Path.GetFileName(Path.GetDirectoryName(path)));
		var request = Assert.Single(_handler.Requests);
		Assert.Equal("https://example.com/pics/cat.png?size=2", request.RequestUri!.AbsoluteUri);
		Assert.False(request.Headers.Contains("Cookie"));
	}

	[Fact]
	public async Task The_name_takes_the_extension_of_what_the_bytes_are()
	{
		_handler.Reply = _ => Bytes(Jpeg, "image/jpeg");
		Assert.Equal("photo.jpg", Path.GetFileName(await Fetch("https://example.com/photo.php")));
		Assert.Equal("img.jpg", Path.GetFileName(await Fetch("https://example.com/img")));
		Assert.Equal("a.jpeg", Path.GetFileName(await Fetch("https://example.com/a.jpeg")));
	}

	[Fact]
	public async Task Two_drops_of_the_same_name_keep_both()
	{
		_handler.Reply = _ => Bytes(Png);
		Assert.NotEqual(await Fetch("https://example.com/cat.png"), await Fetch("https://example.com/cat.png"));
	}

	[Fact]
	public async Task A_base64_data_image_is_decoded()
	{
		var path = await Fetch("data:image/png;base64," + Convert.ToBase64String(Png));
		Assert.Equal(Png, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
		Assert.Equal($"dropped-{Now.ToUnixTimeSeconds()}.png", Path.GetFileName(path));
		Assert.Empty(_handler.Requests);
	}

	[Theory]
	[InlineData("file:///C:/Windows/win.ini")]
	[InlineData("C:\\Windows\\win.ini")]
	[InlineData("ftp://example.com/a.png")]
	[InlineData("javascript:alert(1)")]
	[InlineData("just words")]
	public async Task Anything_but_a_web_address_is_refused_without_a_request(string address)
	{
		var refused = await Assert.ThrowsAsync<PageImageRefusedException>(() => Fetch(address));
		Assert.Contains("http or https", refused.Message);
		Assert.Empty(_handler.Requests);
		Assert.False(Directory.Exists(_root));
	}

	[Theory]
	[InlineData("data:text/html;base64,PGI+")]
	[InlineData("data:image/svg+xml,<svg/>")]
	public async Task A_data_url_that_is_not_a_base64_image_is_refused(string address)
	{
		var refused = await Assert.ThrowsAsync<PageImageRefusedException>(() => Fetch(address));
		Assert.Contains("isn't a picture", refused.Message);
	}

	[Fact]
	public async Task A_page_that_is_not_a_picture_is_refused_and_nothing_is_kept()
	{
		_handler.Reply = _ => Bytes("<!doctype html><title>x</title>"u8.ToArray(), "text/html");
		var refused = await Assert.ThrowsAsync<PageImageRefusedException>(() => Fetch("https://example.com/cat.png"));
		Assert.Contains("isn't a picture", refused.Message);
		Assert.False(Directory.Exists(_root));
	}

	[Fact]
	public async Task A_site_that_refuses_says_so()
	{
		_handler.Reply = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
		var refused = await Assert.ThrowsAsync<PageImageRefusedException>(() => Fetch("https://example.com/cat.png"));
		Assert.Contains("HTTP 403", refused.Message);
	}

	[Fact]
	public async Task A_picture_over_the_limit_is_refused()
	{
		var huge = new byte[PinnedHttp.MaxBodyBytes + 1];
		Png.CopyTo(huge, 0);
		_handler.Reply = _ => Bytes(huge);
		var refused = await Assert.ThrowsAsync<PageImageRefusedException>(() => Fetch("https://example.com/cat.png"));
		Assert.Contains("over 16 MB", refused.Message);
		var data = "data:image/png;base64," + new string('A', (int)((PinnedHttp.MaxBodyBytes + 2) / 3 * 4) + 4);
		Assert.Contains("over 16 MB", (await Assert.ThrowsAsync<PageImageRefusedException>(() => Fetch(data))).Message);
	}

	[Fact]
	public async Task Closing_deletes_every_fetched_picture()
	{
		_handler.Reply = _ => Bytes(Png);
		var first = await Fetch("https://example.com/a.png");
		var second = await Fetch("https://example.com/b.png");
		_drops.Dispose();
		Assert.False(File.Exists(first));
		Assert.False(File.Exists(second));
		Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
		Assert.Empty(_errors);
	}

	[Fact]
	public async Task A_picture_still_held_open_is_reported_when_it_cannot_be_deleted()
	{
		_handler.Reply = _ => Bytes(Png);
		var path = await Fetch("https://example.com/a.png");
		using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) _drops.Dispose();
		var (what, error) = Assert.Single(_errors);
		Assert.Contains(Path.GetDirectoryName(path)!, what);
		Assert.IsType<IOException>(error, exactMatch: false);
	}
}
