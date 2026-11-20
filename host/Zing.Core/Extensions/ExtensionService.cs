namespace Zing.Core.Extensions;

/// <summary>An extension as the engine reports it.</summary>
public sealed record EngineExtension(string Id, string Name, bool IsEnabled);

/// <summary>The browsing profile's extensions, through the engine (WebView2's CoreWebView2Profile). A fake in tests.</summary>
public interface IExtensionEngine
{
	Task<IReadOnlyList<EngineExtension>> ListAsync();
	/// <summary>Loads an unpacked extension from this folder; the engine keeps it in the profile until it is removed.</summary>
	Task<EngineExtension> AddAsync(string folder);
	Task SetEnabledAsync(string id, bool enabled);
	Task RemoveAsync(string id);
}

/// <summary>One row of Settings' Extensions list.</summary>
/// <param name="Key">Names the extension in the other calls.</param>
/// <param name="HasPopup">Its action has a popup, so the toolbar can open it.</param>
/// <param name="Problem">Why it can't be listed properly (its copy's manifest no longer reads); null when fine.</param>
public sealed record ExtensionRow(string Key, string Name, string Version, bool Enabled, bool HasPopup, string? Problem);

/// <param name="Running">Extensions run this launch: Allow extensions and Stay signed in to sites were both on when Zing started.</param>
public sealed record ExtensionsState(bool Running, IReadOnlyList<ExtensionRow> Items);

/// <summary>
/// Settings' Extensions: Load unpacked, the list, enable and disable, remove, and the toolbar's popups. Extensions run
/// only in the persistent profile: the engine installs them in the in-memory one too, but there their content scripts
/// and request rules do nothing (Chromium keeps extensions off in incognito, and WebView2 can't allow them). So they
/// run only when both settings were on at launch; otherwise the copies wait in the store and the engine is never asked.
/// </summary>
public sealed class ExtensionService(ExtensionStore store, bool running)
{
	private TaskCompletionSource<IExtensionEngine> _engine = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private Task? _attached;

	/// <summary>The engine was rebuilt after a crash: the next page's profile is a new object, so it attaches afresh.</summary>
	public void Detach()
	{
		_attached = null;
		if (_engine.Task.IsCompleted) _engine = new(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	public bool Running => running;

	/// <summary>
	/// The browsing profile is ready: loads what the store has that the engine lacks (a profile deleted and made again),
	/// matches each one's on/off to the store, and forgets removed ones. Only the first call does anything; later pages
	/// await the same work, so no page loads before its extensions are there.
	/// </summary>
	public Task AttachAsync(IExtensionEngine engine)
	{
		if (!running) throw new ExtensionsNotRunningException();
		return _attached ??= ReconcileAsync(engine);
	}

	/// <summary>A failure fails the calls waiting for the engine too, rather than leaving them waiting.</summary>
	private async Task ReconcileAsync(IExtensionEngine engine)
	{
		try
		{
			await LoadMissingAsync(engine);
			_engine.SetResult(engine);
		}
		catch (Exception error)
		{
			_engine.SetException(error);
			throw;
		}
	}

	private async Task LoadMissingAsync(IExtensionEngine engine)
	{
		var loaded = (await engine.ListAsync()).ToDictionary(e => e.Id, StringComparer.Ordinal);
		foreach (var id in store.Forget.ToList())
		{
			if (loaded.Remove(id)) await engine.RemoveAsync(id);
			store.Forgotten(id);
		}
		foreach (var item in store.Items.ToList())
		{
			var current = item.Id is { } id && loaded.TryGetValue(id, out var found) ? found : await AddAsync(engine, item.Key);
			if (current.IsEnabled != item.Enabled) await engine.SetEnabledAsync(current.Id, item.Enabled);
		}
	}

	private async Task<EngineExtension> AddAsync(IExtensionEngine engine, string key)
	{
		var added = await engine.AddAsync(store.FolderOf(key));
		store.SetId(key, added.Id);
		return added;
	}

	/// <summary>Waits for <see cref="AttachAsync"/> to have finished; throws when extensions aren't running this launch.</summary>
	private Task<IExtensionEngine> EngineAsync() => running ? _engine.Task : throw new ExtensionsNotRunningException();

	public ExtensionsState State() => new(running, [.. store.Items.Select(Row)]);

	private ExtensionRow Row(InstalledExtension item)
	{
		try
		{
			var manifest = store.ManifestOf(item.Key);
			return new ExtensionRow(item.Key, manifest.Name, manifest.Version, item.Enabled, manifest.Popup is not null, null);
		}
		catch (InvalidExtensionException error)
		{
			return new ExtensionRow(item.Key, item.Key, "", item.Enabled, false, error.Message);
		}
	}

	/// <summary>
	/// Copies the folder in and, while extensions run, loads it now. Loading the same folder again is an update: the old
	/// copy is unloaded first, since the engine reads its files.
	/// </summary>
	public async Task<ExtensionRow> InstallAsync(string source)
	{
		ExtensionManifest.Read(source);
		var full = Path.GetFullPath(source);
		var previous = store.Items.FirstOrDefault(e => string.Equals(e.Source, full, StringComparison.OrdinalIgnoreCase));
		if (running && previous?.Id is { } oldId) await (await EngineAsync()).RemoveAsync(oldId);
		var (item, _) = store.Install(full);
		if (!running) return Row(item);
		var engine = await EngineAsync();
		var added = await AddAsync(engine, item.Key);
		if (added.IsEnabled != item.Enabled) await engine.SetEnabledAsync(added.Id, item.Enabled);
		return Row(store.Get(item.Key));
	}

	public async Task SetEnabledAsync(string key, bool enabled)
	{
		var item = store.Get(key);
		if (running && item.Id is { } id) await (await EngineAsync()).SetEnabledAsync(id, enabled);
		store.SetEnabled(key, enabled);
	}

	public async Task RemoveAsync(string key)
	{
		var item = store.Get(key);
		var forgot = false;
		if (running && item.Id is { } id)
		{
			await (await EngineAsync()).RemoveAsync(id);
			forgot = true;
		}
		store.Remove(key, forgot);
	}

	/// <summary>The extension's action popup page, to open in the popup host; it must be running and turned on.</summary>
	public async Task<string> PopupUrlAsync(string key)
	{
		var item = store.Get(key);
		var manifest = store.ManifestOf(key);
		if (manifest.Popup is not { } popup) throw new NoExtensionPopupException(manifest.Name);
		await EngineAsync();
		var id = store.Get(key).Id ?? throw new ExtensionsNotRunningException();
		if (!item.Enabled) throw new InvalidOperationException($"{manifest.Name} is turned off");
		return $"chrome-extension://{id}/{popup}";
	}
}
