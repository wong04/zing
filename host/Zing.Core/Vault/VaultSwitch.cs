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

	/// <param name="open">Opens the store; throws <see cref="VaultFileException"/> when its files can't be read.</param>
	/// <param name="unreadable">Told when an open failed.</param>
	public VaultSwitch(SettingsStore settings, Func<VaultStore> open, Action<VaultFileException> unreadable)
	{
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

	public void Dispose() => _subscription?.Dispose();
}
