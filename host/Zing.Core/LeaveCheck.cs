namespace Zing.Core;

/// <summary>
/// Runs pages' beforeunload before the person's close takes them (a tab, a window, Quit, Relaunch to update), as Chrome
/// does: a page with unsaved work asks "Leave site?" in the page UI (<see cref="ScriptDialogs"/>), and Stay on any page
/// calls the whole close off. A page that doesn't answer within <see cref="AnswerWait"/> (a hung renderer, a busy script)
/// can't hold the close: it closes as if it had asked nothing. A page whose prompt is up waits for the person.
/// </summary>
public sealed class LeaveCheck(IScheduler scheduler, ScriptDialogs dialogs)
{
	/// <summary>Long enough for a busy page's handler; a person waiting on a close notices little more.</summary>
	public static readonly TimeSpan AnswerWait = TimeSpan.FromSeconds(3);

	/// <summary>True when every page may close; false once the person chose Stay on one, or <paramref name="stay"/> called the close off.</summary>
	public Task<bool> MayAllLeaveAsync(IReadOnlyList<IPage> pages, CancellationToken stay) =>
		AllAsync([.. pages.Select(page => (Func<CancellationToken, Task<bool>>)(token => MayLeaveAsync(page, token)))], stay);

	private async Task<bool> MayLeaveAsync(IPage page, CancellationToken stay)
	{
		// Called off: the page's prompt goes too, answered Stay, whether it is up yet or not.
		using var calledOff = stay.Register(() => dialogs.Stay(page));
		var run = page.RunBeforeUnloadAsync(stay);
		if (run.IsCompleted) return await run;
		var waited = new TaskCompletionSource();
		using (scheduler.Schedule(AnswerWait, () => waited.TrySetResult()))
		{
			if (await Task.WhenAny(run, waited.Task) == run) return await run;
		}
		return !dialogs.IsAsking(page) || await run;
	}

	/// <summary>
	/// Runs every check at once and is true when all are. The first false calls the others off through their token, so
	/// one Stay takes the other pages' (and windows') prompts down rather than leaving them asking about a close that won't happen.
	/// </summary>
	public static async Task<bool> AllAsync(IReadOnlyList<Func<CancellationToken, Task<bool>>> checks, CancellationToken stay)
	{
		using var together = CancellationTokenSource.CreateLinkedTokenSource(stay);
		var running = checks.Select(check => check(together.Token)).ToList();
		while (running.Count > 0)
		{
			var done = await Task.WhenAny(running);
			running.Remove(done);
			if (await done) continue;
			together.Cancel();
			return false;
		}
		return !stay.IsCancellationRequested;
	}
}

/// <summary>
/// The confirm before closing a window of tabs or quitting (<see cref="WindowClose"/>), and its "Don't ask again", which
/// turns <see cref="Setting"/> off; Settings turns it back on.
/// </summary>
public sealed class CloseConfirm(SettingsStore settings)
{
	public const string Setting = "confirmWindowClose";

	private bool Asks => settings.Get(Setting)?.GetValue<bool>() ?? throw new InvalidDataException($"Setting {Setting} is missing");

	/// <summary><see cref="WindowClose.Question"/>, or null while the person has said not to ask.</summary>
	public string? Question(IReadOnlyList<SessionTab> tabs, bool lastWindow, bool tabsReopen) => Asks ? WindowClose.Question(tabs, lastWindow, tabsReopen) : null;

	/// <summary><see cref="WindowClose.QuitQuestion"/>, or null while the person has said not to ask.</summary>
	public string? QuitQuestion(IReadOnlyList<IReadOnlyList<SessionTab>> windows, bool tabsReopen) => Asks ? WindowClose.QuitQuestion(windows, tabsReopen) : null;

	/// <summary>The person ticked "Don't ask again" and went ahead.</summary>
	public void StopAsking() => settings.Set(Setting, false);
}
