using Microsoft.UI.Dispatching;
using Zing.Core;

namespace Zing.Host;

/// <summary>Delayed work on the window's UI thread, where every Zing.Core object lives (none of them lock).</summary>
public sealed class DispatcherScheduler(DispatcherQueue dispatcher) : IScheduler
{
	public IDisposable Schedule(TimeSpan delay, Action action)
	{
		if (!dispatcher.HasThreadAccess) throw new InvalidOperationException("Work was scheduled off the UI thread, where the stores it reads don't lock");
		var timer = dispatcher.CreateTimer();
		timer.Interval = delay;
		timer.IsRepeating = false;
		timer.Tick += (_, _) =>
		{
			timer.Stop();
			TestHooks.Guard(action, "scheduled work");
		};
		timer.Start();
		return new Stop(timer);
	}

	private sealed class Stop(DispatcherQueueTimer timer) : IDisposable
	{
		public void Dispose() => timer.Stop();
	}
}
