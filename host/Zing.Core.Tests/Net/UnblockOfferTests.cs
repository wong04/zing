using System.Text.Json;
using Zing.Core.Net;

namespace Zing.Core.Tests.Net;

/// <summary>The offer after a failed load, and the channels the Open a Link tool uses: nothing runs on a failure, and a third party waits for a yes.</summary>
public sealed class UnblockOfferTests : IDisposable
{
	private sealed class NoProbes : IProbes
	{
		public Task<IReadOnlyList<string>> ResolveSystemAsync(string host, CancellationToken ct) => throw new InvalidOperationException("probed");
		public Task<bool> TcpAsync(string ip, CancellationToken ct) => throw new InvalidOperationException("probed");
		public Task<TlsProbeResult> TlsAsync(string ip, string host, CancellationToken ct) => throw new InvalidOperationException("probed");
	}

	private sealed class NoDoh : IDohClient
	{
		public Task<IReadOnlyList<string>> ResolveAsync(string host, string provider, CancellationToken ct) => throw new InvalidOperationException("asked DoH");
	}

	private readonly Bridge _bridge = new();
	private readonly FakeWindowHost _host = new();
	private readonly RelayEngineTests.FakeTransport _transport = new();
	private readonly RelayConsent _consent = new();
	private readonly ChromeApi _api;

	public UnblockOfferTests()
	{
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		var app = new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), _consent, Net: new NetServices(new NoProbes(), new NoDoh(), _transport));
		_api = new ChromeApi(_bridge, app, _host, "test");
		_api.Start([], "about:blank");
	}

	public void Dispose() => _api.Dispose();

	private async Task<JsonElement> Invoke(string channel, params object?[] args)
	{
		var json = JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel, args });
		return JsonDocument.Parse(await _bridge.HandleAsync(json)).RootElement;
	}

	private static string ErrorName(JsonElement result) => result.GetProperty("error").GetProperty("name").GetString()!;

	private void Fail(string url, string status)
	{
		var tab = _api.Tabs.Open(url);
		((FakePage)_api.Tabs.PageOf(tab.Id)!).Events.LoadFailed(status);
	}

	[Fact]
	public void A_page_that_fails_like_a_block_gets_the_offer_and_nothing_is_tried()
	{
		Fail("https://blocked.test/page", "HostNameNotResolved");
		Assert.Equal(["https://blocked.test/page"], _host.Offers);
		Assert.Empty(_host.Notices);
		Assert.Empty(_transport.Calls);
	}

	[Theory]
	[InlineData("https://down.test/", "ErrorHttpInvalidServerResponse")]
	[InlineData("https://down.test/", "Unknown")]
	[InlineData("zing-relay://down.test/", "HostNameNotResolved")]
	[InlineData("zing://home", "CannotConnect")]
	public void Other_failures_keep_the_plain_notice(string url, string status)
	{
		Fail(url, status);
		Assert.Empty(_host.Offers);
		Assert.Contains(status, Assert.Single(_host.Notices));
	}

	[Fact]
	public void The_offer_shows_the_address_without_a_password()
	{
		Fail("https://me:secret@blocked.test/page", "Timeout");
		var shown = Assert.Single(_host.Offers);
		Assert.DoesNotContain("secret", shown);
		Assert.Contains("blocked.test/page", shown);
	}

	[Theory]
	[InlineData("openLink", "section:Open a Link")]
	[InlineData("diagnose", "section:Diagnose")]
	public async Task Choosing_a_tool_opens_it_with_the_address_filled_in_and_runs_nothing(string tool, string panel)
	{
		Fail("https://blocked.test/page", "CannotConnect");
		Assert.True((await Invoke("offer:choose", tool)).GetProperty("ok").GetBoolean());
		var opened = Assert.Single(_api.Shell.PanelsState().Panels);
		Assert.Equal(panel, opened.Content);
		Assert.Equal("https://blocked.test/page", opened.Input!.Value);
		Assert.Equal(1, _host.OfferDismissals);
		Assert.Empty(_transport.Calls);
	}

	[Fact]
	public async Task A_button_pressed_after_the_offer_went_does_nothing()
	{
		Assert.Equal(nameof(NoOfferError), ErrorName(await Invoke("offer:choose", "openLink")));
		Fail("https://blocked.test/", "Timeout");
		Assert.True((await Invoke("offer:dismiss")).GetProperty("ok").GetBoolean());
		Assert.Equal(nameof(NoOfferError), ErrorName(await Invoke("offer:choose", "openLink")));
		Assert.Empty(_api.Shell.PanelsState().Panels);
	}

	[Fact]
	public async Task An_unknown_tool_is_refused()
	{
		Fail("https://blocked.test/", "Timeout");
		Assert.Equal(nameof(UnknownOfferToolError), ErrorName(await Invoke("offer:choose", "download")));
	}

	[Fact]
	public async Task Third_parties_are_off_until_the_channel_grants_them_and_a_decline_never_calls_it()
	{
		var asked = (await Invoke("relay:open", "example.com", "run-1")).GetProperty("value");
		Assert.Equal("needsConsent", asked.GetProperty("outcome").GetString());
		Assert.False(_consent.ThirdPartiesAllowed);
		Assert.DoesNotContain(_transport.Calls, c => c.Request.Url.Host is "r.jina.ai" or "archive.org");
		Assert.True((await Invoke("relay:allow-third-parties")).GetProperty("ok").GetBoolean());
		Assert.True(_consent.ThirdPartiesAllowed);
	}

	[Fact]
	public async Task The_answer_names_the_way_and_the_address_but_never_carries_the_page()
	{
		_transport.Plain = _ => RelayEngineTests.Ok("<p>SECRET-PAGE-TEXT</p>", url: "http://example.com/final");
		var reply = await Invoke("relay:open", "example.com/a", "run-1");
		Assert.DoesNotContain("SECRET-PAGE-TEXT", reply.ToString());
		var value = reply.GetProperty("value");
		Assert.Equal("direct", value.GetProperty("strategy").GetString());
		Assert.Equal("https://example.com/a", value.GetProperty("address").GetString());
		Assert.False(value.TryGetProperty("content", out _));
		Assert.False(value.TryGetProperty("finalUrl", out _));
	}

	[Fact]
	public async Task A_relayed_tab_refuses_an_http_address_and_opens_nothing()
	{
		var before = _api.Tabs.Records.Count;
		Assert.Equal(nameof(RelayedAddressError), ErrorName(await Invoke("tabs:open-relayed", "http://example.com/")));
		Assert.Equal(before, _api.Tabs.Records.Count);
		Assert.True((await Invoke("tabs:open-relayed", "https://example.com/")).GetProperty("ok").GetBoolean());
		Assert.StartsWith("zing-relay://", _api.Tabs.Records[^1].Url);
	}
}
