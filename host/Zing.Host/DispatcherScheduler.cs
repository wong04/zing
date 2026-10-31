using Microsoft.UI.Dispatching;
using Zing.Core;

namespace Zing.Host;

/// <summary>Delayed work on the window's UI thread, where every Zing.Core object lives (none of them lock).</summary>
public sealed class DispatcherScheduler(DispatcherQueue dispatcher) : IScheduler
{
	public IDisposable Schedule(TimeSpan delay, Action action)
	{
		var timer = dispatcher.CreateTimer();
		timer.Interval = delay;
		timer.IsRepeating = false;
		timer.Tick += (_, _) =>
		{
			timer.Stop();
			action();
		};
		timer.Start();
		return new Stop(timer);
	}

	private sealed class Stop(DispatcherQueueTimer timer) : IDisposable
	{
		public void Dispose() => timer.Stop();
	}
}
