namespace Zing.Core;

/// <summary>
/// Saves every window's tabs, oldest window first, while restoreTabs is on, and removes the
/// file as soon as it is turned off (session-restore.ts SessionSaver). One saver for all windows,
/// so two windows never overwrite each other.
/// </summary>
public sealed class SessionSaver : IDisposable
{
	private readonly string _path;
	private readonly SettingsStore _settings;
	private readonly Func<IReadOnlyList<IReadOnlyList<SessionTab>>> _windows;
	private readonly DebouncedJsonWriter _writer;
	private readonly IDisposable _subscription;
	private bool _keepFile;

	public SessionSaver(string path, SettingsStore settings, Func<IReadOnlyList<IReadOnlyList<SessionTab>>> windows, IScheduler scheduler, Action<Exception> onError)
	{
		_path = path;
		_settings = settings;
		_windows = windows;
		_writer = new DebouncedJsonWriter(path, TimeSpan.FromMilliseconds(500), scheduler, onError);
		_subscription = settings.Subscribe((_, key) =>
		{
			if (key != "restoreTabs") return;
			if (Enabled) Changed();
			else Stop();
		});
	}

	private bool Enabled => _settings.Get("restoreTabs")?.GetValue<bool>() ?? throw new InvalidDataException("Setting restoreTabs is missing");

	/// <summary>Call whenever any window's tabs change, or a window opens or closes.</summary>
	public void Changed()
	{
		if (!Enabled) return;
		var file = Session.ToFile(_windows());
		if (_keepFile && file.Windows.Count == 0) return;
		_keepFile = false;
		_writer.Write(file);
	}

	/// <summary>
	/// Every window's tabs, now, to <paramref name="path"/>, whatever restoreTabs says: the relaunch into an update brings them back
	/// once (<see cref="Session.TakeOnce"/>). A write that fails throws, so the relaunch doesn't go ahead without them.
	/// </summary>
	public void SaveOnce(string path) => JsonFiles.WriteJsonAtomic(path, Session.ToFile(_windows()));

	/// <summary>
	/// The file couldn't be read and was left as it is, as the person was told: it stays until there is a web page to save in
	/// its place, so a window of blank tabs doesn't replace tabs that may still be there.
	/// </summary>
	public void KeepUnreadFile() => _keepFile = true;

	/// <summary>On quit, so the last change inside the debounce window isn't lost.</summary>
	public void Flush() => _writer.Flush();

	public void Dispose() => _subscription.Dispose();

	private void Stop()
	{
		// Flush first so no pending write can recreate the file after it is deleted.
		_writer.Flush();
		Session.Discard(_path);
	}
}
