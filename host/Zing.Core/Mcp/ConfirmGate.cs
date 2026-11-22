namespace Zing.Core.Mcp;

public enum ConfirmAnswer { Allow, Deny, AlwaysAllowSite }

/// <summary>
/// Holds an AI action until the owner answers the confirm bar. Anything but an explicit yes (deny, timeout, the tab being
/// unshared, a newer ask on the same tab) resolves false.
/// </summary>
public sealed class ConfirmGate
{
	public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

	private sealed record Pending(TaskCompletionSource<bool> Result, Uri Url, IDisposable Timer);

	private readonly IScheduler _scheduler;
	private readonly AiShares _shares;
	private readonly SitePolicy _policy;
	private readonly Dictionary<TabRef, Pending> _pending = [];

	public ConfirmGate(IScheduler scheduler, AiShares shares, SitePolicy policy)
	{
		_scheduler = scheduler;
		_shares = shares;
		_policy = policy;
		shares.Unshared += tab => Resolve(tab, false);
	}

	public event Action<TabRef, string>? Shown;
	public event Action<TabRef>? Closed;

	public Task<bool> Ask(TabRef tab, Uri url, string description)
	{
		if (!_shares.IsShared(tab)) return Task.FromResult(false);
		Resolve(tab, false);
		// Not RunContinuationsAsynchronously: callers must resume through the UI thread's synchronization context.
		var result = new TaskCompletionSource<bool>();
		var timer = _scheduler.Schedule(Timeout, () => Resolve(tab, false));
		_pending[tab] = new Pending(result, url, timer);
		Shown?.Invoke(tab, description);
		return result.Task;
	}

	public void Answer(TabRef tab, ConfirmAnswer answer)
	{
		if (!_pending.TryGetValue(tab, out var pending)) return;
		if (answer == ConfirmAnswer.AlwaysAllowSite) _policy.Set(pending.Url, SiteLevel.Never);
		Resolve(tab, answer != ConfirmAnswer.Deny);
	}

	public IReadOnlyList<TabRef> Waiting() => _pending.Keys.ToList();

	private void Resolve(TabRef tab, bool allowed)
	{
		if (!_pending.Remove(tab, out var pending)) return;
		pending.Timer.Dispose();
		Closed?.Invoke(tab);
		pending.Result.SetResult(allowed);
	}
}
