namespace Zing.Core.Vault;

/// <summary>
/// The vault, open only while <c>passwordAutofill</c> is on, following the setting as it changes (Ruling 30): turning it
/// off drops the store, so nothing can read a password until it is turned on again, which opens the store afresh. A
/// vault that can't be read is left as it is, since it may hold the only copy of the passwords: it stays off and says so.
/// </summary>
public sealed class VaultSwitch : IDisposable
{
	private const string Setting = "passwordAutofill";

	private readonly Func<VaultStore>? _open;
	private readonly Action<VaultFileException>? _unreadable;
	private readonly IDisposable? _subscription;
	private readonly Func<bool>? _filesExist;
	private readonly Func<bool>? _syncOnDisk;

	/// <param name="open">Opens the store; throws <see cref="VaultFileException"/> when its files can't be read.</param>
	/// <param name="unreadable">Told when an open failed.</param>
	/// <param name="filesExist">Whether the vault file is on disk, for the times no store is open.</param>
	/// <param name="syncOnDisk">Whether keys.bin holds sync keys, for the times no store is open (<see cref="VaultKeys.SyncOnDisk"/>).</param>
	public VaultSwitch(SettingsStore settings, Func<VaultStore> open, Action<VaultFileException> unreadable, Func<bool>? filesExist = null, Func<bool>? syncOnDisk = null)
	{
		_filesExist = filesExist;
		_syncOnDisk = syncOnDisk;
		_open = open;
		_unreadable = unreadable;
		Follow(On(settings.Get(Setting)));
		_subscription = settings.Subscribe((next, key) =>
		{
			if (key != Setting) return;
			Follow(On(next[Setting]));
			Changed?.Invoke();
		});
	}

	private VaultSwitch(VaultStore? vault, bool unreadable)
	{
		Current = vault;
		Unreadable = unreadable;
	}

	/// <summary>A vault that never follows the setting: tests, and windows built around a store already open.</summary>
	public static VaultSwitch Fixed(VaultStore? vault, bool unreadable) => new(vault, unreadable);

	/// <summary>The open store; null while the setting is off, or the files couldn't be read.</summary>
	public VaultStore? Current { get; private set; }

	/// <summary>The vault's files are on disk, whatever the setting says, found without opening the store. An unreadable vault has files by definition.</summary>
	public bool FilesExist => Unreadable || Current is { Records.Count: > 0 } || (_filesExist?.Invoke() ?? false);

	/// <summary>This PC turned sync on (keys.bin holds its keys, stored or pending), found without opening the store when it's off.</summary>
	public bool SyncOnDisk => Current is { } open ? open.SyncKeys is not null || open.PendingSyncKeys is not null : _syncOnDisk?.Invoke() ?? false;

	/// <summary>The setting is on but the vault's files couldn't be read.</summary>
	public bool Unreadable { get; private set; }

	/// <summary>The vault was turned on or off.</summary>
	public event Action? Changed;

	private static bool On(System.Text.Json.Nodes.JsonNode? value) =>
		value?.GetValue<bool>() ?? throw new InvalidDataException($"Setting {Setting} is missing");

	private void Follow(bool on)
	{
		Unreadable = false;
		if (!on)
		{
			// Disposed before any new store can open, so a write still waiting on Hello is refused rather than landing after it.
			Current?.Dispose();
			Current = null;
			return;
		}
		if (Current is not null) return;
		try
		{
			Current = _open!();
		}
		catch (VaultFileException error)
		{
			Unreadable = true;
			_unreadable!(error);
		}
	}

	/// <summary>Stops following the setting; a store this switch opened is closed with it.</summary>
	public void Dispose()
	{
		if (_subscription is null) return;
		_subscription.Dispose();
		Current?.Dispose();
		Current = null;
	}
}
