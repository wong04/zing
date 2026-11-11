using System.Text.Json;
using Zing.Core.Import;
using Zing.Core.Net;

namespace Zing.Core.Tests.Import;

/// <summary>import:watch-passwords, import:cancel-watch and import:recycle over a fake launcher, trash and scheduler.</summary>
public sealed class ImportPasswordWatchTests : IDisposable
{
	private const string Csv = "name,url,username,password,note\r\nExample,https://example.com/,me,secret,\r\n";

	private sealed class FakeLauncher : IBrowserLauncher
	{
		public List<(ImportBrowser Browser, string Url)> Opened { get; } = [];
		public void Open(ImportBrowser browser, string url) => Opened.Add((browser, url));
	}

	private sealed class FakeTrash : IFileTrash
	{
		public List<string> Recycled { get; } = [];
		public void Recycle(string path) => Recycled.Add(path);
	}

	private readonly string _root = Directory.CreateTempSubdirectory("zing-importwatch-").FullName;
	private readonly string _downloads;
	private readonly Bridge _bridge = new();
	private readonly FakeWindowHost _host = new();
	private readonly FakeLauncher _launcher = new();
	private readonly FakeTrash _trash = new();
	private readonly ChromeApi _api;

	public ImportPasswordWatchTests()
	{
		_downloads = Path.Combine(_root, "Downloads");
		Directory.CreateDirectory(_downloads);
		_host.DownloadsDirectory = _downloads;
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		_api = new ChromeApi(_bridge, new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new RelayConsent(), LocalAppData: () => _root, BrowserLauncher: _launcher, FileTrash: _trash), _host, "test");
		var userData = Path.Combine(_root, "Google", "Chrome", "User Data");
		Directory.CreateDirectory(Path.Combine(userData, "Default"));
		File.WriteAllText(Path.Combine(userData, "Local State"), JsonSerializer.Serialize(new { profile = new { info_cache = new Dictionary<string, object> { ["Default"] = new { name = "Personal" } } } }));
		File.WriteAllText(Path.Combine(userData, "Default", "Bookmarks"), "{}");
		var arc = Path.Combine(_root, "Packages", "TheBrowserCompany.Arc_abc", "LocalCache", "Local", "Arc");
		Directory.CreateDirectory(arc);
		File.WriteAllText(Path.Combine(arc, "StorableSidebar.json"), "{}");
	}

	public void Dispose()
	{
		_api.Dispose();
		Directory.Delete(_root, recursive: true);
	}

	private async Task<JsonElement> Invoke(string channel, params object?[] args)
	{
		var json = JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel, args });
		return JsonDocument.Parse(await _bridge.HandleAsync(json)).RootElement;
	}

	private static void AssertOk(JsonElement result) => Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());

	private static string ErrorName(JsonElement result)
	{
		Assert.False(result.GetProperty("ok").GetBoolean());
		return result.GetProperty("error").GetProperty("name").GetString()!;
	}

	private void Poll(int times) => _host.FakeScheduler.Advance(TimeSpan.FromSeconds(times));

	private void WriteExport(string name)
	{
		var path = Path.Combine(_downloads, name);
		File.WriteAllText(path, Csv);
		File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));
	}

	private List<(string Channel, object? Payload)> Events(string channel) => [.. _host.Pushed.Where(p => p.Channel == channel)];

	[Fact]
	public async Task WatchOpensTheExportPageAndReportsTheFoundFileAsAToken()
	{
		AssertOk(await Invoke("import:watch-passwords", "chrome:Default"));
		Assert.Equal([(ImportBrowser.Chrome, "chrome://password-manager/settings")], _launcher.Opened);

		WriteExport("Chrome Passwords.csv");
		Poll(1);
		Assert.Empty(Events("import:password-export"));
		Poll(1);

		var found = Assert.Single(Events("import:password-export")).Payload;
		var json = JsonSerializer.SerializeToElement(found, new JsonSerializerOptions(JsonSerializerDefaults.Web));
		Assert.Equal("Chrome Passwords.csv", json.GetProperty("name").GetString());
		Assert.DoesNotContain(_root, json.GetRawText());
		Assert.Equal(0, _host.FakeScheduler.PendingCount);

		AssertOk(await Invoke("import:recycle", json.GetProperty("token").GetString()));
		Assert.Equal([Path.Combine(_downloads, "Chrome Passwords.csv")], _trash.Recycled);
	}

	[Fact]
	public async Task ArcHasNoExportPage()
	{
		Assert.Equal("NoPasswordExportException", ErrorName(await Invoke("import:watch-passwords", "arc")));
		Assert.Empty(_launcher.Opened);
		Assert.Equal(0, _host.FakeScheduler.PendingCount);
	}

	[Fact]
	public async Task AnUnknownSourceThrows()
	{
		Assert.Equal("UnknownImportSourceException", ErrorName(await Invoke("import:watch-passwords", "chrome:Nope")));
	}

	[Fact]
	public async Task RecycleResolvesTheToken()
	{
		WriteExport("found.csv");
		AssertOk(await Invoke("import:watch-passwords", "chrome:Default"));
		Poll(1);
		Poll(1);
		var found = JsonSerializer.SerializeToElement(Events("import:password-export").Single().Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));

		AssertOk(await Invoke("import:recycle", found.GetProperty("token").GetString()));

		Assert.Equal([Path.Combine(_downloads, "found.csv")], _trash.Recycled);
		Assert.Equal("UnknownFileGrantException", ErrorName(await Invoke("import:recycle", "not-a-token")));
	}

	[Fact]
	public async Task ANewWatchStopsTheOld()
	{
		AssertOk(await Invoke("import:watch-passwords", "chrome:Default"));
		AssertOk(await Invoke("import:watch-passwords", "chrome:Default"));

		Assert.Equal(1, _host.FakeScheduler.PendingCount);
		Assert.Equal(2, _launcher.Opened.Count);
	}

	[Fact]
	public async Task CancellingStopsTheWatchAndIsIdempotent()
	{
		AssertOk(await Invoke("import:cancel-watch"));
		AssertOk(await Invoke("import:watch-passwords", "chrome:Default"));
		AssertOk(await Invoke("import:cancel-watch"));
		AssertOk(await Invoke("import:cancel-watch"));

		Assert.Equal(0, _host.FakeScheduler.PendingCount);
	}

	[Fact]
	public async Task AFolderRemovedMidWatchPushesTimedOut()
	{
		AssertOk(await Invoke("import:watch-passwords", "chrome:Default"));
		Directory.Delete(_downloads);
		Poll(1);

		Assert.Single(Events("import:watch-timed-out"));
	}
}
