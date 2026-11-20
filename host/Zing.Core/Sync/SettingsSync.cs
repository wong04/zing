using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zing.Core.Vault;

namespace Zing.Core.Sync;

/// <summary>The sync server went back to an older copy of the settings than this PC has seen.</summary>
public sealed class SettingsSyncStoppedException(long served, long seen)
	: Exception($"The sync server's settings are version {served}, but this PC has seen version {seen}, so settings stopped syncing; turn sync off and on again on this PC to start them over.");

/// <summary>
/// Settings, end to end encrypted in one blob slot on the server: the last writer wins. A PC that changed a setting since its last sync
/// writes its settings over the server's, unless the server's were changed later than its own (the time each PC's last change was made,
/// sealed in the blob); a PC that changed nothing takes the server's. Sealed under the settings key (HKDF of the index key) and bound to
/// the version written, so the server can't pass an old blob off as the current one.
/// </summary>
public sealed class SettingsSync : IDisposable
{
	private const string Collection = "settings";
	private const string ItemId = "settings";
	private const int PutAttempts = 3;

	/// <summary>
	/// Settings that stay on each PC. Each needs a relaunch to apply, or belongs to this PC rather than to the person: its install, its
	/// screen, its vault, its first-run history, or what it chose to sync.
	/// </summary>
	public static readonly IReadOnlySet<string> LocalOnly = new HashSet<string>(StringComparer.Ordinal)
	{
		// Applied at the next launch.
		"persistentProfile",
		"restoreTabs",
		"smartScreen",
		"acceleratedCanvas",
		// This PC's: its vault, its install, its screen.
		"passwordAutofill",
		"checkForUpdates",
		"verticalTabWidth",
		// This PC's first-run and usage history.
		"launchedBefore",
		"defaultBrowserDismissed",
		"importNudgeDone",
		"favouritesHint",
		"recentFeatures",
		// What this PC syncs.
		"syncLibrary",
		"syncSettings",
	};

	private static IEnumerable<string> Synced => SettingsSchema.Keys.Where(k => !LocalOnly.Contains(k));

	private readonly VaultStore _vault;
	private readonly SettingsStore _settings;
	private readonly IClock _clock;
	private readonly IDisposable _subscription;
	private bool _applying;

	public SettingsSync(VaultStore vault, SettingsStore settings, IClock clock)
	{
		_vault = vault;
		_settings = settings;
		_clock = clock;
		_subscription = _settings.Subscribe((_, key) =>
		{
			if (_applying) return;
			// Turning either on sends what it covers now; turning one off needs no sync.
			if (key is "syncLibrary" or "syncSettings")
			{
				if (_settings.Get(key)?.GetValue<bool>() == true) Edited?.Invoke();
				return;
			}
			if (LocalOnly.Contains(key)) return;
			NoteChange();
			Edited?.Invoke();
		});
	}

	/// <summary>Whether this PC syncs its settings (the syncSettings setting).</summary>
	public bool On => _settings.Get("syncSettings")?.GetValue<bool>() ?? throw new InvalidDataException("Setting syncSettings is missing");

	public void Dispose() => _subscription.Dispose();

	/// <summary>A synced setting changed here, not from a sync: the engine's debounce follows it.</summary>
	public event Action? Edited;

	/// <summary>One settings sync as <paramref name="user"/>.</summary>
	/// <exception cref="SyncFeatureMissingException">The server is from before settings sync; nothing was sent.</exception>
	public async Task SyncAsync(SyncClient client, string user, CancellationToken cancellation)
	{
		var vaultId = _vault.VaultId ?? throw new InvalidOperationException("The vault has no keys yet");
		var key = _vault.CollectionKey(Collection);
		try
		{
			await RunAsync(client, user, vaultId, key, cancellation);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
		}
	}

	private async Task RunAsync(SyncClient client, string user, string vaultId, byte[] key, CancellationToken cancellation)
	{
		var state = LoadState();
		if (state.UserId != user || state.VaultId != vaultId) state = state with { UserId = user, VaultId = vaultId, Version = 0, Synced = null };
		for (var attempt = 0; attempt < PutAttempts; attempt++)
		{
			var server = await client.GetSettingsAsync(cancellation, asUser: user);
			var served = server?.Version ?? 0;
			if (served < state.Version) throw new SettingsSyncStoppedException(served, state.Version);
			var local = Current();
			var changedHere = state.Synced is null || !JsonNode.DeepEquals(local, state.Synced);

			if (server is not null && served > state.Version)
			{
				var theirs = Open(key, vaultId, server);
				if (!changedHere || theirs.ChangedAt >= state.ChangedAt)
				{
					Apply(theirs.Values);
					SaveState(state with { Version = served, Synced = Current() });
					return;
				}
			}
			else if (!changedHere)
			{
				return;
			}

			var blob = Seal(key, vaultId, served + 1, new Payload(state.ChangedAt, local));
			try
			{
				var version = await client.PutSettingsAsync(blob, served, cancellation, asUser: user);
				if (version != served + 1) throw new SyncProtocolException($"The sync server stored the settings as version {version}, not {served + 1}");
				SaveState(state with { Version = version, Synced = local });
				return;
			}
			catch (SyncConflictException)
			{
				// Another PC wrote meanwhile: read it and decide again.
			}
		}
		throw new SyncManifestBusyException();
	}

	private JsonObject Current()
	{
		var all = _settings.Snapshot();
		var synced = new JsonObject();
		foreach (var key in Synced) synced[key] = all[key]?.DeepClone();
		return synced;
	}

	/// <summary>
	/// The server's settings put in place. A setting this build doesn't know (from a newer Zing) is skipped, as reading settings.json skips
	/// it; a known one with a value this build refuses raises, as it does there.
	/// </summary>
	private void Apply(JsonObject values)
	{
		_applying = true;
		try
		{
			foreach (var (key, value) in values)
			{
				if (!SettingsSchema.IsSettingKey(key) || LocalOnly.Contains(key)) continue;
				_settings.Set(key, value?.DeepClone());
			}
		}
		finally
		{
			_applying = false;
		}
	}

	private static byte[] Seal(byte[] key, string vaultId, long version, Payload payload) =>
		VaultCrypto.Seal(key, JsonSerializer.SerializeToUtf8Bytes(payload, SyncJson.Options), new ItemAd(vaultId, ItemId, version, ItemPart.Summary));

	private static Payload Open(byte[] key, string vaultId, VersionedBlob blob)
	{
		var plain = VaultCrypto.Open(key, blob.Blob, new ItemAd(vaultId, ItemId, blob.Version, ItemPart.Summary));
		var payload = JsonSerializer.Deserialize<Payload>(plain, SyncJson.Options);
		if (payload?.Values is null) throw new SyncProtocolException("The sync server's settings have no values");
		return payload;
	}

	/// <summary>A synced setting changed here: remembered on disk, so a restart before the next sync still counts it as the later write.</summary>
	private void NoteChange()
	{
		SaveState(LoadState() with { ChangedAt = _clock.Now() });
	}

	private string StatePath => _vault.CollectionStateFile(VaultStore.SettingsSyncFileName);

	private SettingsSyncState LoadState()
	{
		if (!File.Exists(StatePath)) return new SettingsSyncState(1, null, null, 0, null, 0);
		var state = JsonSerializer.Deserialize<SettingsSyncState>(File.ReadAllBytes(StatePath), SyncJson.Options) ?? throw new InvalidDataException($"{StatePath} is empty");
		if (state.Format != 1) throw new InvalidDataException($"{StatePath} has format {state.Format}, which this build doesn't read");
		return state;
	}

	private void SaveState(SettingsSyncState state) => JsonFiles.WriteAllBytesAtomic(StatePath, JsonSerializer.SerializeToUtf8Bytes(state, SyncJson.Options));

	/// <param name="ChangedAt">When the settings in it were last changed on the PC that wrote it.</param>
	private sealed record Payload(long ChangedAt, JsonObject Values);
}

/// <summary>
/// settings-sync.json, beside the vault: the server version this PC last read or wrote (its rollback floor), the synced settings as they
/// were then, and when a synced setting last changed here.
/// </summary>
public sealed record SettingsSyncState(int Format, string? UserId, string? VaultId, long Version, JsonObject? Synced, long ChangedAt);
