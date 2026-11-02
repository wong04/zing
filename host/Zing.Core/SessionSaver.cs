namespace Zing.Core;

/// <summary>
/// Saves the primary window's tabs while restoreTabs is on, and removes the
/// file as soon as it is turned off (session-restore.ts SessionSaver).
/// </summary>
public sealed class SessionSaver : IDisposable
{
	private readonly string _path;
	private readonly SettingsStore _settings;
	private readonly Func<IReadOnlyList<SessionTab>> _source;
	private readonly DebouncedJsonWriter _writer;
	private readonly IDisposable _subscription;

	public SessionSaver(string path, SettingsStore settings, Func<IReadOnlyList<SessionTab>> source, IScheduler scheduler, Action<Exception> onError)
	{
		_path = path;
		_settings = settings;
		_source = source;
		_writer = new DebouncedJsonWriter(path, TimeSpan.FromMilliseconds(500), scheduler, onError);
		_subscription = settings.Subscribe((_, key) =>
		{
			if (key != "restoreTabs") return;
			if (Enabled) Changed();
			else Stop();
		});
	}

	private bool Enabled => _settings.Get("restoreTabs")?.GetValue<bool>() ?? throw new InvalidDataException("Setting restoreTabs is missing");

	/// <summary>Call whenever the primary window's tabs change.</summary>
	public void Changed()
	{
		if (!Enabled) return;
		_writer.Write(Session.Prepare(_source()));
	}

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
