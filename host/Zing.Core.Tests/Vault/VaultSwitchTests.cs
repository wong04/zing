using Zing.Core.Vault;

namespace Zing.Core.Tests.Vault;

/// <summary>Turning passwordAutofill off disposes the store: it stops listening to Hello, and a write still waiting can't land after a new store has read the files.</summary>
public sealed class VaultSwitchTests : IDisposable
{
	private readonly string _dir = Directory.CreateTempSubdirectory("zing-vault-switch-").FullName;
	private readonly FakeSealer _sealer = new();
	private readonly SettingsStore _settings = new();
	private readonly VaultSwitch _switch;

	public VaultSwitchTests()
	{
		_settings.Set("passwordAutofill", true);
		_switch = new VaultSwitch(_settings, Open, e => throw new Xunit.Sdk.XunitException($"the vault couldn't be read: {e}"), VaultFilesExist);
	}

	public void Dispose()
	{
		_switch.Dispose();
		Directory.Delete(_dir, recursive: true);
	}

	private bool VaultFilesExist() => File.Exists(Path.Combine(_dir, VaultStore.VaultFileName));

	private VaultStore Open() => new(_dir, new FakeCipher(), _sealer, new TickingClock());

	private void Toggle()
	{
		_settings.Set("passwordAutofill", false);
		_settings.Set("passwordAutofill", true);
	}

	[Fact]
	public void TogglingOffAndOnRepeatedlyLeavesExactlyOneStoreListeningToHello()
	{
		Assert.Equal(1, _sealer.Listeners);

		for (var i = 0; i < 5; i++) Toggle();

		Assert.Equal(1, _sealer.Listeners);
		_settings.Set("passwordAutofill", false);
		Assert.Equal(0, _sealer.Listeners);
	}

	[Fact]
	public async Task TurningTheVaultOffDisposesTheStoreSoItNoLongerReportsHelloChanges()
	{
		var first = _switch.Current!;
		var changes = 0;
		first.Changed += () => changes++;

		_settings.Set("passwordAutofill", false);
		_sealer.Announce();

		Assert.Equal(0, changes);
		await Assert.ThrowsAsync<VaultClosedException>(() => first.SaveAsync(new NewLogin("https://example.com", "ada", "pw", "", "")));
	}

	[Fact]
	public async Task AnUpdateWaitingOnHelloWhenTheVaultIsToggledOffAndOnIsRefusedAndNeverLands()
	{
		var first = _switch.Current!;
		var id = await first.SaveAsync(new NewLogin("https://example.com", "ada", "old password", "", ""));
		_sealer.Unsealing = () =>
		{
			_sealer.Unsealing = null;
			Toggle();
		};

		await Assert.ThrowsAsync<VaultClosedException>(() => first.UpdatePasswordAsync(id, "new password"));

		var now = _switch.Current!;
		Assert.NotSame(first, now);
		Assert.Equal(1, Assert.Single(now.Records).Version);
		Assert.Equal("old password", await now.PasswordAsync(id));
		Assert.Equal(1, Assert.Single(Open().Records).Version);
	}

	[Fact]
	public void ADisposedStoreRefusesSummaryEditsToo()
	{
		var store = Open();
		store.Dispose();

		Assert.Throws<VaultClosedException>(() => store.UpdateSummary("any", "", "", ""));
		Assert.Equal(1, _sealer.Listeners);
	}

	[Fact]
	public async Task VaultFilesAreReportedWhileTheVaultIsOffToo()
	{
		Assert.False(_switch.FilesExist);
		await _switch.Current!.SaveAsync(new NewLogin("https://example.com", "ada", "pw", "", ""));
		Assert.True(_switch.FilesExist);

		_settings.Set("passwordAutofill", false);

		Assert.Null(_switch.Current);
		Assert.True(_switch.FilesExist);
	}

	[Fact]
	public void VaultFilesAreReportedWhenTheyCouldntBeRead()
	{
		File.WriteAllText(Path.Combine(_dir, VaultStore.VaultFileName), "not a vault");
		var settings = new SettingsStore();
		settings.Set("passwordAutofill", true);
		VaultFileException? told = null;

		using var broken = new VaultSwitch(settings, Open, e => told = e, VaultFilesExist);

		Assert.NotNull(told);
		Assert.True(broken.Unreadable);
		Assert.True(broken.FilesExist);
	}
}
