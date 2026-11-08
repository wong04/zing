using Zing.Core.Sync;

namespace Zing.Core.Tests.Sync;

/// <summary>What a build's MSBuild properties (assembly metadata) give sync (Ruling 48).</summary>
public sealed class SyncBuildSettingsTests
{
	private static Func<string, string?> Metadata(params (string Key, string Value)[] values) =>
		key => values.Where(v => v.Key == key).Select(v => v.Value).SingleOrDefault();

	[Fact]
	public void AnEmptyBuildHasNoSync()
	{
		Assert.Null(SyncBuildSettings.Read(Metadata(("ZingSyncServerUrl", "https://zing-server-production.up.railway.app"))));
	}

	[Fact]
	public void AClientIdAndSecretGiveTheServerAndClient()
	{
		var settings = SyncBuildSettings.Read(Metadata(("ZingGoogleClientId", "id"), ("ZingGoogleClientSecret", "secret"), ("ZingSyncServerUrl", "https://example.test")));

		Assert.Equal(new SyncBuildSettings("id", "secret", new Uri("https://example.test")), settings);
	}

	[Fact]
	public void AClientIdWithoutItsSecretIsRefusedAtLaunchNotAfterConsent()
	{
		Assert.Throws<SyncBuildException>(() => SyncBuildSettings.Read(Metadata(("ZingGoogleClientId", "id"), ("ZingSyncServerUrl", "https://example.test"))));
	}

	[Fact]
	public void ASecretWithoutAClientIdIsRefused()
	{
		Assert.Throws<SyncBuildException>(() => SyncBuildSettings.Read(Metadata(("ZingGoogleClientSecret", "secret"), ("ZingSyncServerUrl", "https://example.test"))));
	}

	[Fact]
	public void AClientWithoutAServerIsRefused()
	{
		Assert.Throws<SyncBuildException>(() => SyncBuildSettings.Read(Metadata(("ZingGoogleClientId", "id"), ("ZingGoogleClientSecret", "secret"))));
	}

	[Fact]
	public async Task TurnOnSyncInAnEmptyBuildSaysSyncIsNotSetUp()
	{
		var dir = Directory.CreateTempSubdirectory("zing-emptybuild-").FullName;
		try
		{
			using var vault = new Zing.Core.Vault.VaultStore(dir, new FakeCipher(), new Vault.FakeSealer(), new TickingClock(), new ManualScheduler(), new Vault.FakeSessionEvents());
			var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
			var bridge = new Bridge();
			using var api = new ChromeApi(bridge, new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new Zing.Core.Net.RelayConsent(), Vault: vault, Sync: SyncSetup.FromBuild(Metadata(), (_, _) => throw new InvalidOperationException("an empty build makes no sign-in"))), new FakeWindowHost(), "test");

			var result = System.Text.Json.JsonDocument.Parse(await bridge.HandleAsync("""{"kind":"invoke","id":1,"channel":"sync:sign-in","args":[]}""")).RootElement;

			Assert.Equal("Sync isn't set up in this build of Zing", result.GetProperty("error").GetProperty("message").GetString());
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}
}
