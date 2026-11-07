using System.Text.Json;
using Zing.Core.Net;
using Zing.Core.Vault;

namespace Zing.Core.Tests.Vault;

/// <summary>The vault channels over the real bridge: passwords only after Hello, and vault:state follows every change.</summary>
public sealed class VaultBridgeTests : IDisposable
{
	private readonly string _dir = Directory.CreateTempSubdirectory("zing-vaultbridge-").FullName;
	private readonly FakeSealer _sealer = new();
	private readonly Bridge _bridge = new();
	private readonly Bridge _offBridge = new();
	private readonly FakeWindowHost _host = new();
	private readonly ChromeApi _api;
	private readonly ChromeApi _off;
	private readonly VaultStore _vault;

	public VaultBridgeTests()
	{
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		_vault = new VaultStore(_dir, new FakeCipher(), _sealer, new TickingClock(), new ManualScheduler(), new FakeSessionEvents());
		_api = new ChromeApi(_bridge, new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new RelayConsent(), Vault: _vault), _host, "test");
		_off = new ChromeApi(_offBridge, new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new RelayConsent()), new FakeWindowHost(), "test");
	}

	public void Dispose()
	{
		_api.Dispose();
		_off.Dispose();
		Directory.Delete(_dir, recursive: true);
	}

	private static async Task<JsonElement> Invoke(Bridge bridge, string channel, params object?[] args)
	{
		var json = JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel, args });
		return JsonDocument.Parse(await bridge.HandleAsync(json)).RootElement;
	}

	private static JsonElement Value(JsonElement result)
	{
		if (!result.GetProperty("ok").GetBoolean()) throw new Xunit.Sdk.XunitException(result.GetProperty("error").ToString());
		return result.TryGetProperty("value", out var value) ? value : throw new Xunit.Sdk.XunitException(result.ToString());
	}

	private static void Ok(JsonElement result)
	{
		if (!result.GetProperty("ok").GetBoolean()) throw new Xunit.Sdk.XunitException(result.GetProperty("error").ToString());
	}

	private static string ErrorName(JsonElement result) => result.GetProperty("error").GetProperty("name").GetString()!;

	private static readonly object Login = new { origin = "https://example.com/login", username = "ada", password = "hunter22", title = "", note = "" };

	private List<VaultState> PushedStates() => [.. _host.Pushed.Where(p => p.Channel == "vault:state").Select(p => (VaultState)p.Payload!)];

	[Fact]
	public async Task SaveListRevealAndEditOverTheBridge()
	{
		var id = Value(await Invoke(_bridge, "vault:save", Login)).GetString()!;

		var summary = Value(await Invoke(_bridge, "vault:summaries"))[0];
		Assert.Equal("https://example.com", summary.GetProperty("origin").GetString());
		Assert.False(summary.TryGetProperty("password", out _));
		Assert.Equal("hunter22", Value(await Invoke(_bridge, "vault:reveal", id)).GetString());

		Ok(await Invoke(_bridge, "vault:update", id, new { username = "ada2", title = "Example", note = "", password = "new one" }));
		Assert.Equal("new one", Value(await Invoke(_bridge, "vault:reveal", id)).GetString());
		Assert.Equal("hunter22", Value(await Invoke(_bridge, "vault:reveal-history", id, 0)).GetString());
		Assert.Equal(1, Value(await Invoke(_bridge, "vault:history", id)).GetArrayLength());
		Assert.Equal("ada2", Value(await Invoke(_bridge, "vault:summaries"))[0].GetProperty("username").GetString());

		Ok(await Invoke(_bridge, "vault:delete", id));
		Assert.Equal(JsonValueKind.Number, Value(await Invoke(_bridge, "vault:summaries"))[0].GetProperty("deletedAt").ValueKind);
		Ok(await Invoke(_bridge, "vault:restore", id));
		Assert.Equal(JsonValueKind.Null, Value(await Invoke(_bridge, "vault:summaries"))[0].GetProperty("deletedAt").ValueKind);
	}

	[Fact]
	public async Task AnEditWithAPasswordIsOneChange()
	{
		var id = Value(await Invoke(_bridge, "vault:save", Login)).GetString()!;

		Ok(await Invoke(_bridge, "vault:update", id, new { username = "ada2", title = "t", note = "n", password = "new one" }));

		Assert.Equal(2, _vault.Records.Single().Version);
		Assert.Equal(2, _vault.Queued.Count);
		Assert.Equal("ada2", _vault.Summaries().Single().Username);
	}

	[Fact]
	public async Task AVaultThatCouldntBeReadSaysSoInItsState()
	{
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		var bridge = new Bridge();
		using var api = new ChromeApi(bridge, new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new RelayConsent(), VaultUnreadable: true), new FakeWindowHost(), "test");

		var state = Value(await Invoke(bridge, "vault:get"));

		Assert.False(state.GetProperty("enabled").GetBoolean());
		Assert.Equal("unreadable", state.GetProperty("error").GetString());
		Assert.True(state.GetProperty("filesExist").GetBoolean());
		Assert.Equal(nameof(VaultOffException), ErrorName(await Invoke(bridge, "vault:summaries")));
	}

	[Fact]
	public async Task AnEditWithoutAPasswordNeverAsksHello()
	{
		var id = Value(await Invoke(_bridge, "vault:save", Login)).GetString()!;
		var calls = _sealer.Calls;

		Ok(await Invoke(_bridge, "vault:update", id, new { username = "ada", title = "t", note = "n" }));

		Assert.Equal(calls, _sealer.Calls);
	}

	[Fact]
	public async Task ACancelledPasswordEditLeavesTheSummaryAsItWas()
	{
		var id = Value(await Invoke(_bridge, "vault:save", Login)).GetString()!;
		_sealer.Result = HelloResult.Cancel;

		var result = await Invoke(_bridge, "vault:update", id, new { username = "changed", title = "t", note = "n", password = "x" });

		Assert.Equal(nameof(HelloCancelledException), ErrorName(result));
		Assert.Equal("ada", Value(await Invoke(_bridge, "vault:summaries"))[0].GetProperty("username").GetString());
	}

	[Fact]
	public async Task AResetHelloKeyAnswersRevealCopyAndSaveWithTheSetUpAgainError()
	{
		var id = Value(await Invoke(_bridge, "vault:save", Login)).GetString()!;
		_sealer.UnsealFailure = new VaultTamperedException("the Hello key was reset");

		foreach (var result in new[]
		{
			await Invoke(_bridge, "vault:reveal", id),
			await Invoke(_bridge, "vault:copy", id),
			await Invoke(_bridge, "vault:save", new { origin = "https://other.example", username = "bob", password = "pw", title = "", note = "" }),
			await Invoke(_bridge, "vault:update", id, new { username = "ada", title = "t", note = "n", password = "x" }),
		})
		{
			Assert.Equal(nameof(VaultSetUpAgainException), ErrorName(result));
			Assert.Equal("Set up the vault again on this PC", result.GetProperty("error").GetProperty("message").GetString());
		}
		Assert.Single(Value(await Invoke(_bridge, "vault:summaries")).EnumerateArray());
	}

	[Fact]
	public async Task VaultStateIsPushedAfterEveryChange()
	{
		Assert.Equal(new VaultState(true, true, 0), Value(await Invoke(_bridge, "vault:get")).Deserialize<VaultState>(ZingJson.Options));

		var id = Value(await Invoke(_bridge, "vault:save", Login)).GetString()!;
		await Invoke(_bridge, "vault:delete", id);
		_sealer.Result = HelloResult.Unavailable;
		_sealer.Announce();

		// A login in Recently deleted isn't counted.
		Assert.Equal([new VaultState(true, true, 1, null, true), new VaultState(true, true, 0, null, true), new VaultState(true, false, 0, null, true)], PushedStates());
	}

	[Fact]
	public async Task AClosedWindowStopsGettingVaultState()
	{
		var host = new FakeWindowHost();
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		new ChromeApi(new Bridge(), new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new RelayConsent(), Vault: _vault), host, "test").Dispose();

		await Invoke(_bridge, "vault:save", Login);

		Assert.Single(PushedStates());
		Assert.DoesNotContain(host.Pushed, p => p.Channel == "vault:state");
	}

	[Fact]
	public async Task WithTheVaultOffEveryCallButTheStateIsRefused()
	{
		Assert.Equal(new VaultState(false, false, 0), Value(await Invoke(_offBridge, "vault:get")).Deserialize<VaultState>(ZingJson.Options));
		Assert.Equal(nameof(VaultOffException), ErrorName(await Invoke(_offBridge, "vault:summaries")));
		Assert.Equal(nameof(VaultOffException), ErrorName(await Invoke(_offBridge, "vault:save", Login)));
		Assert.Equal(nameof(VaultOffException), ErrorName(await Invoke(_offBridge, "vault:reveal", "x")));
	}
}
