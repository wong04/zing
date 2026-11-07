using System.Text;
using System.Text.Json;
using Zing.Core.Net;
using Zing.Core.Vault;

namespace Zing.Core.Tests.Vault;

/// <summary>vault:import-csv, vault:export-csv and the never-saved list over the real bridge.</summary>
public sealed class VaultCsvBridgeTests : IDisposable
{
	private readonly string _dir = Directory.CreateTempSubdirectory("zing-vaultcsvbridge-").FullName;
	private readonly FakeSealer _sealer = new();
	private readonly Bridge _bridge = new();
	private readonly FakeWindowHost _host = new();
	private readonly ChromeApi _api;
	private readonly VaultStore _vault;

	public VaultCsvBridgeTests()
	{
		Directory.CreateDirectory(Downloads);
		_host.DownloadsDirectory = Downloads;
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		_vault = new VaultStore(Path.Combine(_dir, "vault"), new FakeCipher(), _sealer, new TickingClock());
		_api = new ChromeApi(_bridge, new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new RelayConsent(), Vault: _vault), _host, "test");
	}

	public void Dispose()
	{
		_api.Dispose();
		Directory.Delete(_dir, recursive: true);
	}

	private string Downloads => Path.Combine(_dir, "downloads");

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

	private static string ErrorName(JsonElement result) => result.GetProperty("error").GetProperty("name").GetString()!;

	/// <summary>A file on disk the way the picker would hand it over: as a token.</summary>
	private async Task<string> Pick(string text)
	{
		var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.csv");
		File.WriteAllText(path, text);
		return await PickPath(path);
	}

	private async Task<string> PickPath(string path)
	{
		_host.PickedPaths = [path];
		return Value(await Invoke(_bridge, "files:pick", (object)new[] { "csv" }))[0].GetProperty("token").GetString()!;
	}

	// Review Focus 5: two duplicate rows, a blank password, an android:// row and "not a url".
	private const string Chrome = "name,url,username,password,note\n" +
		"site,https://site.example/login,ada,first,\n" +
		"site again,https://site.example/other,ada,first,\n" +
		"blank,https://blank.example,ada,,\n" +
		"app,android://hash@com.app/,ada,pw,\n" +
		"bad,not a url,ada,pw,\n";

	[Fact]
	public async Task TheCsvPickerOffersCsvFiles()
	{
		await Pick("x");

		Assert.Equal(["csv"], _host.PickedExtensions);
	}

	[Fact]
	public async Task AChromeCsvWithDuplicatesAndBadRowsMergesAndListsEachSkippedRowWithItsReason()
	{
		var token = await Pick(Chrome);

		var result = Value(await Invoke(_bridge, "vault:import-csv", token));

		Assert.Equal(1, result.GetProperty("added").GetInt32());
		Assert.Equal(1, result.GetProperty("merged").GetInt32());
		Assert.Equal(
			["4:No password", "5:Not a web address: android://hash@com.app/", "6:Not a web address: not a url"],
			result.GetProperty("skipped").EnumerateArray().Select(s => $"{s.GetProperty("row").GetInt32()}:{s.GetProperty("reason").GetString()}"));
		var login = Assert.Single(_vault.Summaries());
		Assert.Equal("https://site.example", login.Origin);
		Assert.Equal("first", await _vault.PasswordAsync(login.Id));
	}

	[Fact]
	public async Task ImportAsksHelloOnce()
	{
		await _vault.SaveAsync(new NewLogin("https://x.example", "u", "p", "", ""));
		var token = await Pick(Chrome);
		var calls = _sealer.Calls;

		await Invoke(_bridge, "vault:import-csv", token);

		Assert.Equal(calls + 1, _sealer.Calls);
	}

	[Fact]
	public async Task ACsvWithNoUsableRowAsksNoHello()
	{
		var token = await Pick("name,url,username,password,note\nblank,https://blank.example,ada,,\n");

		var result = Value(await Invoke(_bridge, "vault:import-csv", token));

		Assert.Equal(0, result.GetProperty("added").GetInt32());
		Assert.Equal(1, result.GetProperty("skipped").GetArrayLength());
		Assert.Equal(0, _sealer.Calls);
	}

	[Fact]
	public async Task AnUnknownTokenImportsNothing()
	{
		Assert.Equal(nameof(Zing.Core.Engines.UnknownFileGrantException), ErrorName(await Invoke(_bridge, "vault:import-csv", "made-up")));
	}

	[Fact]
	public async Task ACsvThatIsNotAPasswordExportIsRefusedByName()
	{
		var token = await Pick("a,b\n1,2\n");

		Assert.Equal(nameof(UnknownPasswordCsvException), ErrorName(await Invoke(_bridge, "vault:import-csv", token)));
	}

	[Fact]
	public async Task AnOverLargeCsvIsRefusedByName()
	{
		var path = Path.Combine(_dir, "big.csv");
		using (var file = File.Create(path)) file.SetLength(VaultCsv.MaxFileBytes + 1);
		var token = await PickPath(path);

		Assert.Equal(nameof(CsvTooLargeException), ErrorName(await Invoke(_bridge, "vault:import-csv", token)));
	}

	[Fact]
	public async Task ExportAsksHelloOnceAndSavesChromesCsvIntoDownloads()
	{
		await _vault.SaveAsync(new NewLogin("https://a.example/login", "ada", "pw,1", "A", "n"));
		await _vault.SaveAsync(new NewLogin("https://b.example", "bob", "pw2", "", ""));
		var calls = _sealer.Calls;

		var saved = Value(await Invoke(_bridge, "vault:export-csv"));

		Assert.Equal(calls + 1, _sealer.Calls);
		Assert.Equal("Zing Passwords.csv", saved.GetProperty("name").GetString());
		var file = Path.Combine(Downloads, "Zing Passwords.csv");
		Assert.Equal("name,url,username,password,note\r\nA,https://a.example,ada,\"pw,1\",n\r\nb.example,https://b.example,bob,pw2,\r\n", File.ReadAllText(file, Encoding.UTF8));
		Assert.Equal([file], Directory.GetFiles(Downloads));
	}

	[Fact]
	public async Task ExportNeverReplacesAnEarlierFileWhileNeverOverwriteIsOn()
	{
		await _vault.SaveAsync(new NewLogin("https://a.example", "ada", "pw", "", ""));

		await Invoke(_bridge, "vault:export-csv");
		var second = Value(await Invoke(_bridge, "vault:export-csv"));

		Assert.Equal("Zing Passwords 2.csv", second.GetProperty("name").GetString());
	}

	[Fact]
	public async Task ACancelledExportWritesNoFile()
	{
		await _vault.SaveAsync(new NewLogin("https://a.example", "ada", "pw", "", ""));
		_sealer.Result = HelloResult.Cancel;

		Assert.Equal(nameof(HelloCancelledException), ErrorName(await Invoke(_bridge, "vault:export-csv")));
		Assert.Empty(Directory.GetFileSystemEntries(Downloads));
	}

	[Fact]
	public async Task NeverSavedSitesAreListedAndRemovedOverTheBridgeWithNoHello()
	{
		await _vault.NeverSave("https://never.example/login");
		var calls = _sealer.Calls;

		var listed = Value(await Invoke(_bridge, "vault:never-saves"));
		Assert.Equal("https://never.example", Assert.Single(listed.EnumerateArray()).GetProperty("origin").GetString());
		Assert.True((await Invoke(_bridge, "vault:remove-never-save", "https://never.example")).GetProperty("ok").GetBoolean());

		Assert.Equal(0, Value(await Invoke(_bridge, "vault:never-saves")).GetArrayLength());
		Assert.Equal(calls, _sealer.Calls);
	}
}
