using System.Text.Json;
using Zing.Core.Engines;
using Zing.Core.Import;

namespace Zing.Core.Tests.Import;

/// <summary>The New Tab card's state: which card, what the buttons do, and what survives a trip to Settings.</summary>
public sealed class NudgeTests : IDisposable
{
	private sealed class Hooks
	{
		public bool Done { get; set; }
		public string? ProgId { get; set; }
		public List<string> Opened { get; } = [];
		public int Reloads { get; set; }
		public int ProgIdReads { get; set; }

		public NudgeHost Host => new(() => Done, () => Done = true, () => { ProgIdReads++; return ProgId; }, Opened.Add, () => Reloads++);
	}

	private readonly string _root = Directory.CreateTempSubdirectory("zing-nudge-").FullName;
	private readonly Hooks _hooks = new() { ProgId = "ChromeHTML" };
	private readonly LibraryStore _library = new(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
	private readonly ImportApi _api;

	public NudgeTests()
	{
		_api = new ImportApi(_library, new FileGrants(), () => _root, () => throw new InvalidOperationException("no launcher"), () => throw new InvalidOperationException("no trash"), new ManualScheduler(), _root, (_, _) => { }, _hooks.Host);
		WriteChrome("Default", "Personal", "chromium-bookmarks.json");
		WriteChrome("Profile 1", "Work", "chromium-bookmarks.json", lastUsed: "Profile 1");
	}

	public void Dispose()
	{
		_api.Dispose();
		Directory.Delete(_root, recursive: true);
	}

	private void WriteChrome(string profile, string name, string fixture, string? lastUsed = null)
	{
		var userData = Path.Combine(_root, "Google", "Chrome", "User Data");
		Directory.CreateDirectory(Path.Combine(userData, profile));
		var statePath = Path.Combine(userData, "Local State");
		var names = File.Exists(statePath) ? JsonSerializer.Deserialize<Dictionary<string, object>>(JsonDocument.Parse(File.ReadAllText(statePath)).RootElement.GetProperty("profile").GetProperty("info_cache").GetRawText())! : [];
		names[profile] = new { name };
		File.WriteAllText(statePath, JsonSerializer.Serialize(new { profile = new { info_cache = names, last_used = lastUsed ?? "Default" } }));
		File.WriteAllText(Path.Combine(userData, profile, "Bookmarks"), File.ReadAllText(Path.Combine(Vectors.RepoRoot(), "src/shared/import/fixtures", fixture)));
	}

	[Fact]
	public void OfferNamesTheDefaultBrowsersLastUsedProfile()
	{
		var offer = Assert.IsType<ImportNudge.Offer>(_api.Nudge());
		Assert.Equal("Chrome", offer.Browser);
		Assert.Equal("Work", offer.Profile);
		Assert.Equal("chrome:Profile 1", offer.SourceId);
		Assert.True(offer.Bookmarks > 0);
		Assert.True(offer.Passwords);
	}

	[Theory]
	[InlineData("ZingHTML")]
	[InlineData(null)]
	public void ZingAsDefaultGivesTheGenericCard(string? progId)
	{
		_hooks.ProgId = progId;
		Assert.Equal(new ImportNudge.Generic(), _api.Nudge());
	}

	[Fact]
	public void TheOfferIsReadOnceAndThenKept()
	{
		_api.Nudge();
		_api.Nudge();
		Assert.Equal(1, _hooks.ProgIdReads);
	}

	[Fact]
	public void DismissHidesItForGood()
	{
		_api.Act("dismiss", null);
		Assert.True(_hooks.Done);
		Assert.Equal(1, _hooks.Reloads);
		Directory.Delete(_root, recursive: true);
		Assert.Null(_api.Nudge());
		Assert.Equal(0, _hooks.ProgIdReads);
		Directory.CreateDirectory(_root);
	}

	[Fact]
	public void ImportShowsTheResultThenDoneHidesIt()
	{
		_api.Act("bookmarks", "chrome:Profile 1");
		var result = Assert.IsType<ImportNudge.Result>(_api.Nudge());
		Assert.Equal("Chrome", result.Browser);
		Assert.Equal("chrome:Profile 1", result.SourceId);
		Assert.True(result.Added > 0);
		Assert.True(_hooks.Done);
		Assert.Equal(1, _hooks.Reloads);

		_api.Act("dismiss", null);
		Assert.Null(_api.Nudge());
		Assert.Equal(2, _hooks.Reloads);
	}

	[Fact]
	public void PasswordsTooLeavesAPendingFlowForSettings()
	{
		_api.Act("bookmarks", "chrome:Profile 1");
		_api.Act("passwords", "chrome:Profile 1");
		Assert.Equal(["import"], _hooks.Opened);
		Assert.Null(_api.Nudge());
		Assert.Equal(new PendingImport("passwords", "chrome:Profile 1"), _api.TakePending());
		Assert.Null(_api.TakePending());
	}

	[Fact]
	public void SettingsJustOpensSettings()
	{
		_api.Act("settings", null);
		Assert.Equal(["import"], _hooks.Opened);
		Assert.False(_hooks.Done);
	}

	[Fact]
	public void AFailedImportPutsTheGenericCardBackWithTheError()
	{
		File.WriteAllText(Path.Combine(_root, "Google", "Chrome", "User Data", "Profile 1", "Bookmarks"), "{ not json");
		_api.Act("bookmarks", "chrome:Profile 1");
		var card = Assert.IsType<ImportNudge.Generic>(_api.Nudge());
		Assert.False(string.IsNullOrEmpty(card.Error));
		Assert.DoesNotContain(_root, card.Error);
		Assert.False(_hooks.Done);
		Assert.Equal(1, _hooks.Reloads);
	}
}
