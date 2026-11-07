using System.Text.Json;
using Zing.Core.Net;
using Zing.Core.Vault;

namespace Zing.Core.Tests.Vault;

/// <summary>
/// The account picker and fill, through a window's real ChromeApi and bridge: the picker follows the frame's own
/// source (what WebView2 reports), never the message, and a password reaches the page only after a choice and Hello.
/// </summary>
public sealed class AutofillTests : IDisposable
{
	private const string Site = "https://example.com";
	private static readonly Rect FieldRect = new(40, 120, 200, 28);

	private readonly string _dir = Directory.CreateTempSubdirectory("zing-autofill-").FullName;
	private readonly FakeSealer _sealer = new();
	private readonly Bridge _bridge = new();
	private readonly FakeWindowHost _host = new();
	private readonly VaultStore _vault;
	private readonly ChromeApi _api;

	public AutofillTests()
	{
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		_vault = new VaultStore(_dir, new FakeCipher(), _sealer, new TickingClock());
		_api = new ChromeApi(_bridge, new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new RelayConsent(), Vault: _vault), _host, "test");
		_api.Start([], "about:blank");
	}

	public void Dispose()
	{
		_api.Dispose();
		Directory.Delete(_dir, recursive: true);
	}

	private string TabId => _api.Tabs.ActiveId!;
	private FakePage Page => (FakePage)_api.Tabs.ActivePage!;

	private async Task<string> Save(string origin, string username, string password = "hunter22") =>
		await _vault.SaveAsync(new NewLogin(origin, username, password, "", ""));

	private void OnPage(string url) => Page.Url = url;

	private void Focus(string frameSource, CredentialFieldKind kind = CredentialFieldKind.Username) =>
		Page.Events.Autofill(new Uri(frameSource), new AutofillMessage.Focused(new CredentialField(kind, FieldRect)));

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

	[Fact]
	public async Task FocusingASignInFieldShowsTheUsernamesSavedForThatOriginWithoutHello()
	{
		var id = await Save($"{Site}/login", "ada");
		await Save("https://other.test", "eve");
		var calls = _sealer.Calls;
		OnPage($"{Site}/login");

		Focus($"{Site}/login");

		Assert.Equal(new AccountPicker(TabId, FieldRect, [new PickerEntry(id, "ada")]), Picker, new PickerComparer());
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
		var forged = AutofillMessage.Parse("""{"zingCredentialField":{"kind":"username","x":1,"y":2,"w":100,"h":20,"origin":"https://other.test","frameUrl":"https://other.test/"}}""");
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
	public void NoPickerWithTheVaultOff()
	{
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		var host = new FakeWindowHost();
		using var off = new ChromeApi(new Bridge(), new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new RelayConsent()), host, "test");
		off.Start([], "about:blank");
		var page = (FakePage)off.Tabs.ActivePage!;
		page.Url = $"{Site}/login";

		page.Events.Autofill(new Uri($"{Site}/login"), new AutofillMessage.Focused(new CredentialField(CredentialFieldKind.Username, FieldRect)));

		Assert.Null(off.Autofill.Picker);
		Assert.DoesNotContain(host.Pushed, p => p.Channel == "pageui:account-picker" && p.Payload is not null);
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
	public async Task ChoosingAsksHelloOnceThenFillsBothHalvesInThePage()
	{
		var id = await Save(Site, "ada", "pa\"ss</script>");
		OnPage($"{Site}/login");
		Focus($"{Site}/login");
		Page.ProbeResult = JsonDocument.Parse("true").RootElement;
		var unseals = _sealer.Unseals;

		Assert.Equal("filled", Value(await Invoke("vault:fill", TabId, id)).GetString());

		Assert.Equal(unseals + 1, _sealer.Unseals);
		var script = Assert.Single(Page.Evaluated);
		Assert.Equal(AutofillScripts.Fill("ada", "pa\"ss</script>"), script);
		Assert.Null(Picker);
		Assert.Null(Pushed().Last());
		Assert.Equal("focus", Page.Calls.Last());
	}

	[Theory]
	[InlineData(HelloResult.Cancel)]
	[InlineData(HelloResult.Unavailable)]
	public async Task HelloCancelledOrUnavailableFillsNothingRaisesNothingAndClosesThePicker(HelloResult hello)
	{
		var id = await Save(Site, "ada");
		OnPage($"{Site}/login");
		Focus($"{Site}/login");
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
	public async Task AnIdThatWasNotOfferedIsRefusedBeforeHello()
	{
		await Save(Site, "ada");
		var other = await Save("https://other.test", "eve");
		OnPage($"{Site}/login");
		Focus($"{Site}/login");
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
	public async Task EscapeClosesAndArrowDownReopensAndFocusesThePicker()
	{
		await Save(Site, "ada");
		OnPage($"{Site}/login");
		Focus($"{Site}/login");

		Page.Events.Autofill(new Uri($"{Site}/login"), new AutofillMessage.Key(PickerKey.Escape));
		Assert.Null(Picker);

		Page.Events.Autofill(new Uri($"{Site}/login"), new AutofillMessage.Key(PickerKey.Down));
		Assert.NotNull(Picker);
		Assert.Equal(1, _host.PickerFocuses);
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
		Assert.Null(AutofillMessage.Parse("""{"kind":"invoke","id":1,"channel":"tabs:close","args":["t1"]}"""));
		Assert.Null(AutofillMessage.Parse("\"hello\""));
		Assert.IsType<AutofillMessage.Left>(AutofillMessage.Parse("""{"zingCredentialField":{"kind":"none"}}"""));
		Assert.IsType<AutofillMessage.Left>(AutofillMessage.Parse("""{"zingCredentialField":{"kind":"password","x":0,"y":0,"w":0,"h":20}}"""));
		Assert.Equal(new AutofillMessage.Key(PickerKey.Down), AutofillMessage.Parse("""{"zingCredentialField":{"kind":"key","key":"down"}}"""));
		Assert.Equal(new AutofillMessage.Focused(new CredentialField(CredentialFieldKind.Password, new Rect(10, 20.5, 200, 30))),
			AutofillMessage.Parse("""{"zingCredentialField":{"kind":"password","x":10,"y":20.5,"w":200,"h":30}}"""));
	}

	[Theory]
	[InlineData("""{"zingCredentialField":"x"}""")]
	[InlineData("""{"zingCredentialField":{"kind":"secret","x":0,"y":0,"w":1,"h":1}}""")]
	[InlineData("""{"zingCredentialField":{"kind":"username","x":"0","y":0,"w":1,"h":1}}""")]
	[InlineData("""{"zingCredentialField":{"kind":"key","key":"enter"}}""")]
	public void AMalformedAutofillMessageSaysSo(string json)
	{
		Assert.Throws<AutofillMessageException>(() => AutofillMessage.Parse(json));
	}

	[Fact]
	public void TheScriptsComeFromTheTypeScript()
	{
		Assert.Contains("zingCredentialField", AutofillScripts.Detection, StringComparison.Ordinal);
		Assert.Equal("zingCredentialField", AutofillScripts.MessageKey);
		// JSON string literals, so no value can end the call or the script: quotes and angle brackets are escaped.
		Assert.Equal("window.__zingAutofillFill(\"a\\u0022b\", \"\\u003C/script\\u003E\")", AutofillScripts.Fill("a\"b", "</script>"));
	}

	/// <summary>Records compare their lists by reference; this compares the entries.</summary>
	private sealed class PickerComparer : IEqualityComparer<AccountPicker?>
	{
		public bool Equals(AccountPicker? a, AccountPicker? b) =>
			a is not null && b is not null && a.PageId == b.PageId && a.Rect == b.Rect && a.Entries.SequenceEqual(b.Entries);

		public int GetHashCode(AccountPicker? picker) => 0;
	}
}
