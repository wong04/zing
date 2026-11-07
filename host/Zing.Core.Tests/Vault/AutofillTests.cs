using System.Text.Json;
using Zing.Core.Net;
using Zing.Core.Vault;

namespace Zing.Core.Tests.Vault;

/// <summary>
/// The account picker and fill, through a window's real ChromeApi and bridge: the picker follows the frame's own
/// source (what WebView2 reports), never the message; its keys are real key presses the host sees, never the page's;
/// and a password reaches the page only after a choice and Hello.
/// </summary>
public sealed class AutofillTests : IDisposable
{
	private const string Site = "https://example.com";
	private static readonly Rect FieldRect = new(40, 120, 200, 28);
	private static readonly Rect Viewport = new(0, 80, 1280, 740);

	private readonly string _dir = Directory.CreateTempSubdirectory("zing-autofill-").FullName;
	private readonly FakeSealer _sealer = new();
	private readonly Bridge _bridge = new();
	private readonly FakeWindowHost _host = new();
	private readonly TestClock _clock = new(1_000_000);
	private readonly SettingsStore _settings = new();
	private readonly VaultStore _vault;
	private readonly VaultSwitch _switch;
	private readonly ChromeApi _api;
	private int _opens;

	public AutofillTests()
	{
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		_vault = new VaultStore(_dir, new FakeCipher(), _sealer, new TickingClock());
		_settings.Set("passwordAutofill", true);
		_switch = new VaultSwitch(_settings, Open, e => throw new Xunit.Sdk.XunitException($"the vault couldn't be read: {e}"));
		_api = new ChromeApi(_bridge, new AppServices(_settings, library, Address.FromDefaults(), _clock, Accounts.Guest(), new RelayConsent(), Switch: _switch), _host, "test");
		_api.Start([], "about:blank");
	}

	private VaultStore Open()
	{
		_opens++;
		return _vault;
	}

	public void Dispose()
	{
		_api.Dispose();
		_switch.Dispose();
		Directory.Delete(_dir, recursive: true);
	}

	private string TabId => _api.Tabs.ActiveId!;
	private FakePage Page => (FakePage)_api.Tabs.ActivePage!;

	private async Task<string> Save(string origin, string username, string password = "hunter22") =>
		await _vault.SaveAsync(new NewLogin(origin, username, password, "", ""));

	private void OnPage(string url) => Page.Url = url;

	private void Focus(string frameSource, Rect? rect = null) =>
		Page.Events.Autofill(new Uri(frameSource), new AutofillMessage.Focused(new CredentialField(CredentialFieldKind.Username, rect ?? FieldRect, Viewport)));

	/// <summary>Past the click guard: a click counts once the picker has been on screen for 500 ms.</summary>
	private void Wait() => _clock.Jump(500);

	private AccountPicker? Picker => _api.Autofill.Picker;

	private List<AccountPicker?> Pushed() => [.. _host.Pushed.Where(p => p.Channel == "pageui:account-picker").Select(p => (AccountPicker?)p.Payload)];

	private async Task<JsonElement> Invoke(string channel, params object?[] args)
	{
		var json = JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel, args });
		return JsonDocument.Parse(await _bridge.HandleAsync(json)).RootElement;
	}

	private static JsonElement Value(JsonElement result)
	{
		if (!result.GetProperty("ok").GetBoolean()) throw new Xunit.Sdk.XunitException(result.GetProperty("error").ToString());
		return result.TryGetProperty("value", out var value) ? value : throw new Xunit.Sdk.XunitException(result.ToString());
	}

	private static string Fill(string username, string password) => AutofillScripts.Fill(Site, username, password);

	[Fact]
	public async Task FocusingASignInFieldShowsTheUsernamesSavedForThatOriginWithNothingSelectedAndNoHello()
	{
		var id = await Save($"{Site}/login", "ada");
		await Save("https://other.test", "eve");
		var calls = _sealer.Calls;
		OnPage($"{Site}/login");

		Focus($"{Site}/login");

		Assert.Equal(TabId, Picker!.PageId);
		Assert.Equal(FieldRect, Picker.Rect);
		Assert.Equal(Viewport, Picker.Viewport);
		Assert.Equal([new PickerEntry(id, "ada")], Picker.Entries);
		Assert.Equal(-1, Picker.Active);
		Assert.Single(Pushed());
		Assert.Equal(calls, _sealer.Calls);
		Assert.Equal(TabId, _host.Pickers.Single()!.PageId);
	}

	[Fact]
	public async Task TheFrameSourceDecidesTheOriginNotWhatThePageSays()
	{
		await Save(Site, "ada");
		var theirs = await Save("https://other.test", "eve");
		OnPage($"{Site}/login");

		// The message has no say in the origin: one claiming another site's still gets this frame's logins.
		Assert.Equal(PageMessageVerdict.Autofill, AutofillMessage.Read("""{"zingCredentialField":{"kind":"username","x":1,"y":2,"w":100,"h":20,"origin":"https://other.test","frameUrl":"https://other.test/"}}""", out var forged));
		Page.Events.Autofill(new Uri($"{Site}/login"), forged!);

		Assert.Equal(["ada"], Picker!.Entries.Select(e => e.Username));
		Assert.DoesNotContain(Picker.Entries, e => e.Id == theirs);
	}

	[Fact]
	public async Task ACrossOriginFrameNeverGetsAPickerForTheTopPagesLogins()
	{
		await Save(Site, "ada");
		await Save("https://frame.test", "frame-user");
		OnPage($"{Site}/login");

		Focus("https://frame.test/embed");

		Assert.Null(Picker);
		Assert.All(Pushed(), Assert.Null);
	}

	[Fact]
	public async Task NoPickerWhenNothingIsSavedForTheOrigin()
	{
		await Save("https://other.test", "eve");
		OnPage($"{Site}/login");

		Focus($"{Site}/login");

		Assert.Null(Picker);
	}

	[Fact]
	public async Task WithTheSettingOffAtLaunchTheStoreIsNeverOpenedAndAPageWithASavedLoginGetsNoPicker()
	{
		await Save(Site, "ada");
		var settings = new SettingsStore();
		settings.Set("passwordAutofill", false);
		var opens = 0;
		using var off = new VaultSwitch(settings, () =>
		{
			opens++;
			return _vault;
		}, _ => { });
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		var host = new FakeWindowHost();
		using var api = new ChromeApi(new Bridge(), new AppServices(settings, library, Address.FromDefaults(), _clock, Accounts.Guest(), new RelayConsent(), Switch: off), host, "test");
		api.Start([], "about:blank");
		var page = (FakePage)api.Tabs.ActivePage!;
		page.Url = $"{Site}/login";

		page.Events.Autofill(new Uri($"{Site}/login"), new AutofillMessage.Focused(new CredentialField(CredentialFieldKind.Username, FieldRect, Viewport)));

		Assert.Equal(0, opens);
		Assert.Null(off.Current);
		Assert.Null(api.Autofill.Picker);
		Assert.DoesNotContain(host.Pushed, p => p.Channel == "pageui:account-picker" && p.Payload is not null);
	}

	[Fact]
	public async Task TurningTheSettingOffClosesTheStoreAndThePickerAndOnOpensItAgain()
	{
		var id = await Save(Site, "ada");
		OnPage($"{Site}/login");
		Focus($"{Site}/login");
		Assert.NotNull(Picker);
		var switched = 0;
		_switch.Changed += () => switched++;

		_settings.Set("passwordAutofill", false);

		Assert.Null(_switch.Current);
		Assert.Null(Picker);
		Assert.Null(Pushed().Last());
		Assert.Equal(1, switched);
		Assert.False(Value(await Invoke("vault:get")).GetProperty("enabled").GetBoolean());
		_host.FakeScheduler.Advance(TimeSpan.FromMilliseconds(100));
		Focus($"{Site}/login");
		Assert.Null(Picker);
		Assert.Equal("refused", Value(await Invoke("vault:fill", TabId, id)).GetString());

		_settings.Set("passwordAutofill", true);

		Assert.Same(_vault, _switch.Current);
		Assert.Equal(2, _opens);
		_host.FakeScheduler.Advance(TimeSpan.FromMilliseconds(100));
		Focus($"{Site}/login");
		Assert.NotNull(Picker);
	}

	[Fact]
	public void AVaultThatCantBeReadWhenTurnedOnStaysOffAndSaysSo()
	{
		var settings = new SettingsStore();
		settings.Set("passwordAutofill", false);
		VaultFileException? told = null;
		using var vaults = new VaultSwitch(settings, () => throw new VaultFileException("vault.zing", new InvalidDataException("bad")), e => told = e);

		settings.Set("passwordAutofill", true);

		Assert.Null(vaults.Current);
		Assert.True(vaults.Unreadable);
		Assert.NotNull(told);
	}

	[Fact]
	public async Task ANonWebPageGetsNoPicker()
	{
		await Save(Site, "ada");
		OnPage("about:blank");

		Focus("about:blank");

		Assert.Null(Picker);
	}

	[Fact]
	public async Task FocusReportsAreThrottledToOnePickerUpdatePerPagePer100MsAndTheLatestWins()
	{
		await Save(Site, "ada");
		OnPage($"{Site}/login");

		for (var i = 0; i < 50; i++) Focus($"{Site}/login", FieldRect with { Y = i });

		Assert.Single(Pushed());
		Assert.Equal(0, Picker!.Rect.Y);
		_host.FakeScheduler.Advance(TimeSpan.FromMilliseconds(99));
		Assert.Single(Pushed());
		_host.FakeScheduler.Advance(TimeSpan.FromMilliseconds(1));
		Assert.Equal(2, Pushed().Count);
		Assert.Equal(49, Picker!.Rect.Y);
		_host.FakeScheduler.Advance(TimeSpan.FromMilliseconds(500));
		Assert.Equal(2, Pushed().Count);
	}

	[Fact]
	public async Task LeavingTheFieldDropsAReportStillWaitingOutTheThrottle()
	{
		await Save(Site, "ada");
		OnPage($"{Site}/login");
		Focus($"{Site}/login");
		Focus($"{Site}/login", FieldRect with { Y = 5 });

		Page.Events.Autofill(new Uri($"{Site}/login"), new AutofillMessage.Left());
		_host.FakeScheduler.Advance(TimeSpan.FromMilliseconds(100));

		Assert.Null(Picker);
	}

	[Fact]
	public async Task AClickChoosesOnceThePickerHasBeenOnScreenFor500MsThenHelloOnceThenFillsBothHalves()
	{
		var id = await Save(Site, "ada", "pa\"ss</script>");
		OnPage($"{Site}/login");
		Focus($"{Site}/login");
		Page.ProbeResult = JsonDocument.Parse("true").RootElement;
		var unseals = _sealer.Unseals;

		_clock.Jump(499);
		Assert.Equal("ignored", Value(await Invoke("vault:fill", TabId, id)).GetString());
		Assert.NotNull(Picker);
		Assert.Equal(unseals, _sealer.Unseals);

		_clock.Jump(1);
		Assert.Equal("filled", Value(await Invoke("vault:fill", TabId, id)).GetString());

		Assert.Equal(unseals + 1, _sealer.Unseals);
		Assert.Equal(Fill("ada", "pa\"ss</script>"), Assert.Single(Page.Evaluated));
		Assert.Null(Picker);
		Assert.Null(Pushed().Last());
		Assert.Equal("focus", Page.Calls.Last());
	}

	[Fact]
	public async Task ThePickerMovingStartsTheClickGuardAgain()
	{
		var id = await Save(Site, "ada");
		OnPage($"{Site}/login");
		Focus($"{Site}/login");
		Wait();
		_host.FakeScheduler.Advance(TimeSpan.FromMilliseconds(100));

		// A page can move the picker under the pointer just before a click.
		Focus($"{Site}/login", FieldRect with { X = 300 });

		Assert.Equal("ignored", Value(await Invoke("vault:fill", TabId, id)).GetString());
	}

	[Fact]
	public async Task RealKeysMoveTheSelectionEnterChoosesAndEscCloses()
	{
		await Save(Site, "ada");
		await Save(Site, "bob");
		OnPage($"{Site}/login");
		Focus($"{Site}/login");
		Page.ProbeResult = JsonDocument.Parse("true").RootElement;

		// Enter with nothing selected is the page's: not taken, nothing filled.
		Assert.False(_api.Autofill.Key(PickerKey.Enter));
		Assert.Empty(Page.Evaluated);

		Assert.True(_api.Autofill.Key(PickerKey.Down));
		Assert.Equal(0, Picker!.Active);
		Assert.True(_api.Autofill.Key(PickerKey.Down));
		Assert.Equal(1, Picker!.Active);
		Assert.True(_api.Autofill.Key(PickerKey.Down));
		Assert.Equal(0, Picker!.Active);
		Assert.True(_api.Autofill.Key(PickerKey.Up));
		Assert.Equal(1, Picker!.Active);
		Assert.True(_api.Autofill.Key(PickerKey.Up));
		Assert.Equal(0, Picker!.Active);
		var picked = Picker.Entries[0];

		// A real Enter is the user's own choice: no click guard.
		Assert.True(_api.Autofill.Key(PickerKey.Enter));

		Assert.Equal(Fill(picked.Username, "hunter22"), Assert.Single(Page.Evaluated));
		Assert.Null(Picker);

		_host.FakeScheduler.Advance(TimeSpan.FromMilliseconds(100));
		Focus($"{Site}/login");
		Assert.NotNull(Picker);
		Assert.True(_api.Autofill.Key(PickerKey.Escape));
		Assert.Null(Picker);
		Assert.False(_api.Autofill.Key(PickerKey.Down));
	}

	[Fact]
	public void OnlyTheArrowsEnterAndEscAreThePickersKeys()
	{
		Assert.Equal(PickerKey.Up, PickerKeys.FromVirtualKey(0x26));
		Assert.Equal(PickerKey.Down, PickerKeys.FromVirtualKey(0x28));
		Assert.Equal(PickerKey.Enter, PickerKeys.FromVirtualKey(0x0D));
		Assert.Equal(PickerKey.Escape, PickerKeys.FromVirtualKey(0x1B));
		Assert.Null(PickerKeys.FromVirtualKey(0x41));
		Assert.Null(PickerKeys.FromVirtualKey(0x09));
	}

	[Theory]
	[InlineData(HelloResult.Cancel)]
	[InlineData(HelloResult.Unavailable)]
	public async Task HelloCancelledOrUnavailableFillsNothingRaisesNothingAndClosesThePicker(HelloResult hello)
	{
		var id = await Save(Site, "ada");
		OnPage($"{Site}/login");
		Focus($"{Site}/login");
		Wait();
		_sealer.Result = hello;

		var result = await Invoke("vault:fill", TabId, id);

		Assert.Equal("cancelled", Value(result).GetString());
		Assert.Empty(Page.Evaluated);
		Assert.Null(Picker);
		Assert.Null(Pushed().Last());
		Assert.Empty(_host.Reported);
	}

	[Fact]
	public async Task CanFillRefusesAFrameSourceThatDiffersFromTheOriginTheEntriesWereChosenFor()
	{
		var id = await Save(Site, "ada");
		OnPage($"{Site}/login");
		Focus($"{Site}/login");
		Wait();
		var unseals = _sealer.Unseals;
		// The page went elsewhere (a redirect, a script) after the picker opened.
		OnPage("https://evil.test/phish");

		Assert.Equal("refused", Value(await Invoke("vault:fill", TabId, id)).GetString());

		Assert.False(VaultModel.CanFill(new FillContext(Site, "https://evil.test/phish")));
		Assert.Equal(unseals, _sealer.Unseals);
		Assert.Empty(Page.Evaluated);
		Assert.Null(Picker);
	}

	[Fact]
	public async Task ANavigationDuringHelloRefusesTheFill()
	{
		var id = await Save(Site, "ada");
		OnPage($"{Site}/login");
		Focus($"{Site}/login");
		Wait();
		_sealer.Unsealing = () => OnPage("https://evil.test/phish");

		Assert.Equal("refused", Value(await Invoke("vault:fill", TabId, id)).GetString());

		Assert.Empty(Page.Evaluated);
	}

	[Fact]
	public void TheFillOnlyRunsInADocumentStillOnTheChosenOrigin()
	{
		// A navigation that commits between the last check and the script finds another location.origin, and fills nothing.
		Assert.Equal("location.origin === \"https://example.com\" && window.__zingAutofillFill(\"a\\u0022b\", \"\\u003C/script\\u003E\")", AutofillScripts.Fill(Site, "a\"b", "</script>"));
	}

	[Fact]
	public async Task AnIdThatWasNotOfferedIsRefusedBeforeHello()
	{
		await Save(Site, "ada");
		var other = await Save("https://other.test", "eve");
		OnPage($"{Site}/login");
		Focus($"{Site}/login");
		Wait();
		var unseals = _sealer.Unseals;

		var result = await Invoke("vault:fill", TabId, other);

		Assert.Equal(nameof(AutofillEntryException), result.GetProperty("error").GetProperty("name").GetString());
		Assert.Equal(unseals, _sealer.Unseals);
		Assert.Empty(Page.Evaluated);
	}

	[Fact]
	public async Task LeavingTheFieldOrStartingANewDocumentClosesThePicker()
	{
		await Save(Site, "ada");
		OnPage($"{Site}/login");
		Focus($"{Site}/login");
		Assert.NotNull(Picker);

		Page.Events.Autofill(new Uri($"{Site}/login"), new AutofillMessage.Left());

		Assert.Null(Picker);
	}

	[Fact]
	public async Task ThePickerClosesWhenItsTabIsNoLongerShown()
	{
		await Save(Site, "ada");
		OnPage($"{Site}/login");
		Focus($"{Site}/login");

		_api.Tabs.Activate(_api.Tabs.Open("about:blank").Id);

		Assert.Null(Picker);
	}

	[Fact]
	public async Task ClosingFromThePickerHandsTheKeyboardBackToThePage()
	{
		await Save(Site, "ada");
		OnPage($"{Site}/login");
		Focus($"{Site}/login");

		await Invoke("pageui:picker-close");

		Assert.Null(Picker);
		Assert.Equal("focus", Page.Calls.Last());
	}

	[Fact]
	public void MessagesThatArentTheScriptsAreNotAutofillMessages()
	{
		Assert.Equal(PageMessageVerdict.NotAutofill, AutofillMessage.Read("""{"kind":"invoke","id":1,"channel":"tabs:close","args":["t1"]}""", out var m));
		Assert.Null(m);
		Assert.Equal(PageMessageVerdict.NotAutofill, AutofillMessage.Read("\"hello\"", out _));
		Assert.Equal(PageMessageVerdict.Autofill, AutofillMessage.Read("""{"zingCredentialField":{"kind":"none"}}""", out m));
		Assert.IsType<AutofillMessage.Left>(m);
		AutofillMessage.Read("""{"zingCredentialField":{"kind":"password","x":0,"y":0,"w":0,"h":20}}""", out m);
		Assert.IsType<AutofillMessage.Left>(m);
		AutofillMessage.Read("""{"zingCredentialField":{"kind":"password","x":10,"y":20.5,"w":200,"h":30}}""", out m);
		Assert.Equal(new AutofillMessage.Focused(new CredentialField(CredentialFieldKind.Password, new Rect(10, 20.5, 200, 30), default)), m);
	}

	[Theory]
	[InlineData("""{"zingCredentialField":"x"}""")]
	[InlineData("""{"zingCredentialField":{"kind":"secret","x":0,"y":0,"w":1,"h":1}}""")]
	[InlineData("""{"zingCredentialField":{"kind":"username","x":"0","y":0,"w":1,"h":1}}""")]
	[InlineData("""{"zingCredentialField":{"kind":1}}""")]
	// Keys never come from the page (Ruling 28): a key message is not one the host takes.
	[InlineData("""{"zingCredentialField":{"kind":"key","key":"down"}}""")]
	[InlineData("""{"zingCredentialField":{"kind":"key","key":1}}""")]
	[InlineData("""{"zingCredentialField":{"kind":"username","x":1e400,"y":0,"w":1,"h":1}}""")]
	public void AMalformedAutofillMessageIsDroppedWithoutThrowing(string json)
	{
		Assert.Equal(PageMessageVerdict.Dropped, AutofillMessage.Read(json, out var message));
		Assert.Null(message);
	}

	[Fact]
	public void AnOversizedOrTooDeepPageMessageIsDroppedBeforeItIsRead()
	{
		var big = """{"zingCredentialField":{"kind":"username","x":0,"y":0,"w":1,"h":1,"pad":" """ + new string('x', 5000) + "\"}}";
		Assert.Equal(PageMessageVerdict.Dropped, AutofillMessage.Read(big, out _));
		var deep = string.Concat(Enumerable.Repeat("""{"a":""", 100)) + "1" + new string('}', 100);
		Assert.Equal(PageMessageVerdict.Dropped, AutofillMessage.Read(deep, out _));
		Assert.Equal(PageMessageVerdict.Dropped, AutofillMessage.Read("{not json", out _));
		Assert.Equal(4096, AutofillMessage.MaxLength);
	}

	[Fact]
	public void TheScriptsComeFromTheTypeScript()
	{
		Assert.Contains("zingCredentialField", AutofillScripts.Detection, StringComparison.Ordinal);
		Assert.DoesNotContain("'key'", AutofillScripts.Detection, StringComparison.Ordinal);
		Assert.Equal("zingCredentialField", AutofillScripts.MessageKey);
	}
}
