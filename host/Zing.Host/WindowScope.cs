namespace Zing.Host;

/// <summary>
/// Everything a window started: its views (those still loading included), subscriptions and the
/// work that resumes after an await. Created first in <see cref="MainWindow"/>; closing the window
/// closes the scope, which cancels <see cref="Token"/> and then closes everything tracked.
/// Code that resumes after an await calls <see cref="Checkpoint"/>, which throws
/// <see cref="ViewGoneException"/> once the window has gone (an expected outcome, never recorded).
/// UI thread only, like the rest of the host.
/// </summary>
public sealed class WindowScope
{
	private readonly CancellationTokenSource _cancel = new();
	private readonly List<Registration> _tracked = [];
	private int? _closeAt;
	private Action? _closeWindow;

	public bool IsClosed { get; private set; }

	/// <summary>Cancelled when the scope closes, before anything tracked is closed.</summary>
	public CancellationToken Token => _cancel.Token;

	/// <summary>How many checkpoints the window's work has passed, for the e2e close-at hook.</summary>
	public int Checkpoints { get; private set; }

	/// <summary>
	/// Registers something to close with the window. Call it as its creation starts, before any await,
	/// so a window that closes meanwhile still reaches it. Disposing the result forgets it (it closed by itself).
	/// On a closed scope it closes at once.
	/// </summary>
	public IDisposable Track(Action close)
	{
		var registration = new Registration(this, close);
		if (IsClosed) Run(registration);
		else _tracked.Add(registration);
		return registration;
	}

	/// <summary>Where work resumes after an await: counts it (TestHooks close-at), then stops if the window has gone.</summary>
	/// <exception cref="ViewGoneException">The window closed.</exception>
	public void Checkpoint(string what)
	{
		Checkpoints++;
		if (Checkpoints == _closeAt) _closeWindow?.Invoke();
		ThrowIfClosed(what);
	}

	/// <exception cref="ViewGoneException">The window closed.</exception>
	public void ThrowIfClosed(string what)
	{
		if (IsClosed) throw new ViewGoneException(what);
	}

	/// <summary>e2e only (TestHooks.Watch): closes the window at the <paramref name="checkpoint"/>-th checkpoint.</summary>
	internal void CloseAt(int checkpoint, Action closeWindow)
	{
		_closeAt = checkpoint;
		_closeWindow = closeWindow;
	}

	/// <summary>Cancels, then closes everything tracked, newest first; one failure doesn't stop the rest. Idempotent.</summary>
	public void Close()
	{
		if (IsClosed) return;
		IsClosed = true;
		try
		{
			_cancel.Cancel();
		}
		catch (AggregateException error)
		{
			TestHooks.Record("window close", error);
		}
		var tracked = _tracked.ToArray();
		_tracked.Clear();
		for (var i = tracked.Length - 1; i >= 0; i--) Run(tracked[i]);
	}

	private static void Run(Registration registration)
	{
		try
		{
			registration.CloseNow();
		}
		catch (Exception error)
		{
			TestHooks.Record("window close", error);
		}
	}

	private sealed class Registration(WindowScope scope, Action close) : IDisposable
	{
		private bool _done;

		public void CloseNow()
		{
			if (_done) return;
			_done = true;
			close();
		}

		public void Dispose()
		{
			_done = true;
			scope._tracked.Remove(this);
		}
	}
}
