using System.Text.Json;
using Zing.Core;

namespace Zing.Core.Tests;

/// <summary>The local crash log: a line per renderer or engine crash, the last 200 kept, nothing sent anywhere.</summary>
public sealed class CrashLogTests : IDisposable
{
	private readonly string _dir = Directory.CreateTempSubdirectory("zing-crashlog-").FullName;
	private DateTimeOffset _now = new(2026, 11, 20, 9, 30, 0, TimeSpan.Zero);

	public void Dispose() => Directory.Delete(_dir, recursive: true);

	private string LogPath => Path.Combine(_dir, "crash-log.txt");

	private CrashLog Log() => new(LogPath, () => _now, "0.4.2");

	[Fact]
	public void NoCrashMeansNoFile()
	{
		Assert.False(Log().Exists);
		Assert.False(File.Exists(LogPath));
	}

	[Fact]
	public void AppendsTheTimeKindExitCodeAndVersion()
	{
		var log = Log();

		log.Append(CrashKind.Renderer, -1073741819);
		_now = _now.AddMinutes(5);
		log.Append(CrashKind.Engine, 1);

		Assert.True(log.Exists);
		Assert.Equal(
			[
				"2026-11-20T09:30:00Z\trenderer\texit -1073741819 (0xC0000005)\tZing 0.4.2",
				"2026-11-20T09:35:00Z\tengine\texit 1 (0x00000001)\tZing 0.4.2",
			],
			File.ReadAllLines(LogPath));
	}

	[Fact]
	public void AnEngineThatWentUnheardHasNoExitCode()
	{
		Log().Append(CrashKind.Engine, null);

		Assert.Equal(["2026-11-20T09:30:00Z	engine	exit unknown	Zing 0.4.2"], File.ReadAllLines(LogPath));
	}

	[Fact]
	public void CreatesTheFolderItLivesIn()
	{
		var nested = new CrashLog(Path.Combine(_dir, "user data", "crash-log.txt"), () => _now, "0.4.2");

		nested.Append(CrashKind.Renderer, 3);

		Assert.Single(File.ReadAllLines(Path.Combine(_dir, "user data", "crash-log.txt")));
	}

	[Fact]
	public void KeepsOnlyTheLast200Lines()
	{
		var log = Log();
		for (var i = 0; i < 205; i++) log.Append(CrashKind.Renderer, i);

		var lines = File.ReadAllLines(LogPath);

		Assert.Equal(CrashLog.MaxLines, lines.Length);
		Assert.Contains("exit 5 (", lines[0]);
		Assert.Contains("exit 204 (", lines[^1]);
	}

	[Fact]
	public void ClearRemovesTheFile()
	{
		var log = Log();
		log.Append(CrashKind.Engine, 1);

		log.Clear();

		Assert.False(log.Exists);
	}

	[Fact]
	public void ClearingWhenThereIsNoLogDoesNothing() => Log().Clear();

	[Fact]
	public void TheAddressIsTheFileUrl() => Assert.Equal(new Uri(LogPath).AbsoluteUri, Log().Address);
}

/// <summary>about:crash-log, about:open-crash-log and about:clear-crash-log: Settings › About's link to the log, and the privacy banner's view of it.</summary>
public sealed class ChromeApiCrashLogTests : IDisposable
{
	private readonly string _dir = Directory.CreateTempSubdirectory("zing-crashlog-api-").FullName;
	private readonly Bridge _bridge = new();
	private readonly CrashLog _log;
	private readonly ChromeApi _api;

	public ChromeApiCrashLogTests()
	{
		_log = new CrashLog(Path.Combine(_dir, "crash-log.txt"), () => DateTimeOffset.UnixEpoch, "test");
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		_api = new ChromeApi(_bridge, new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new Zing.Core.Net.RelayConsent(), CrashLog: _log), new FakeWindowHost(), "test");
	}

	public void Dispose()
	{
		_api.Dispose();
		Directory.Delete(_dir, recursive: true);
	}

	private async Task<JsonElement> Invoke(string channel)
	{
		var json = JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel, args = Array.Empty<object>() });
		var result = JsonDocument.Parse(await _bridge.HandleAsync(json)).RootElement;
		Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
		return result.TryGetProperty("value", out var value) ? value : default;
	}

	[Fact]
	public async Task SaysWhetherTheLogExists()
	{
		Assert.False((await Invoke("about:crash-log")).GetProperty("exists").GetBoolean());

		_log.Append(CrashKind.Renderer, 1);

		Assert.True((await Invoke("about:crash-log")).GetProperty("exists").GetBoolean());
	}

	[Fact]
	public async Task OpenShowsTheLogInANewTab()
	{
		_log.Append(CrashKind.Renderer, 1);

		await Invoke("about:open-crash-log");

		Assert.Contains(_api.Tabs.Records, r => r.Url == _log.Address);
	}

	[Fact]
	public async Task OpeningALogThatIsNotThereFails()
	{
		var json = JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel = "about:open-crash-log", args = Array.Empty<object>() });
		var result = JsonDocument.Parse(await _bridge.HandleAsync(json)).RootElement;

		Assert.False(result.GetProperty("ok").GetBoolean());
		Assert.Contains("NoCrashLogException", result.ToString());
	}

	[Fact]
	public async Task ClearDeletesTheLog()
	{
		_log.Append(CrashKind.Engine, 1);

		await Invoke("about:clear-crash-log");

		Assert.False(_log.Exists);
	}
}
