using System.Text.Json;
using Zing.Core;

namespace Zing.Core.Tests;

/// <summary>about:credits: About's Credits sheet shows the third-party notices file the build generated.</summary>
public sealed class ChromeApiCreditsTests : IDisposable
{
	private readonly Bridge _bridge = new();
	private readonly FakeWindowHost _host = new();
	private readonly ChromeApi _api;

	public ChromeApiCreditsTests()
	{
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		_api = new ChromeApi(_bridge, new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new Zing.Core.Net.RelayConsent()), _host, "test");
	}

	public void Dispose() => _api.Dispose();

	private async Task<JsonElement> Invoke()
	{
		var json = JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel = "about:credits", args = Array.Empty<object>() });
		return JsonDocument.Parse(await _bridge.HandleAsync(json)).RootElement;
	}

	[Fact]
	public async Task Answers_with_the_notices_file()
	{
		var path = Path.Combine(Path.GetTempPath(), $"zing-notices-{Guid.NewGuid():N}.txt");
		File.WriteAllText(path, "Third-party notices\n@mozilla/readability 0.6.0\n");
		try
		{
			_host.ThirdPartyNoticesPath = path;
			var result = await Invoke();
			Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
			Assert.Equal("Third-party notices\n@mozilla/readability 0.6.0\n", result.GetProperty("value").GetString());
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public async Task A_missing_file_fails_naming_it()
	{
		_host.ThirdPartyNoticesPath = Path.Combine(Path.GetTempPath(), "zing-no-such-notices.txt");
		var result = await Invoke();
		Assert.False(result.GetProperty("ok").GetBoolean());
		Assert.Contains("zing-no-such-notices.txt", result.ToString());
	}
}
