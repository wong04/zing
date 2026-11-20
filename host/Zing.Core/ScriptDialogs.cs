namespace Zing.Core;

/// <summary>The JavaScript dialogs a page can open: alert, confirm, prompt, and beforeunload's "Leave site?".</summary>
public static class ScriptDialogKinds
{
	public const string Alert = "alert";
	public const string Confirm = "confirm";
	public const string Prompt = "prompt";
	public const string BeforeUnload = "beforeunload";

	public static readonly IReadOnlySet<string> All = new HashSet<string> { Alert, Confirm, Prompt, BeforeUnload };
}

/// <param name="Origin">The asking page's origin, as <see cref="PermissionBroker"/> names it ("null" when it has no web origin).</param>
/// <param name="DefaultText">A prompt's starting text; null for the other kinds.</param>
/// <param name="Tab">The asking tab's title when it isn't the tab on screen, so a background tab's dialog says whose it is.</param>
public sealed record ScriptDialogPrompt(int Id, string Kind, string Origin, string Message, string? DefaultText = null, string? Tab = null);

/// <summary>The engine's dialog, waiting for its answer. The host implements it over the event's deferral.</summary>
public interface IScriptDialogAnswer
{
	/// <summary>OK, or Leave for beforeunload; <paramref name="text"/> is what a prompt hands back.</summary>
	void Accept(string? text);
	/// <summary>Cancel, or Stay for beforeunload.</summary>
	void Dismiss();
}

/// <summary>
/// A window's JavaScript dialogs, drawn by Zing in the page UI the way permission prompts are, never by the engine. One shows
/// at a time, in the order they came; each blocks only its own page's script.
/// </summary>
/// <param name="hiddenTabOf">The title of the tab a page belongs to when that tab isn't on screen; null when it is shown.</param>
public sealed class ScriptDialogs(PageUiStore ui, Func<object, string?> hiddenTabOf)
{
	private sealed record Pending(int Id, object Page, string Kind, string Origin, string Message, string? DefaultText, IScriptDialogAnswer Answer);

	private readonly List<Pending> _queue = [];
	private int _nextId = 1;
	// The dialog Showing was raised for, so a tab change it causes doesn't raise it again.
	private int _announced;

	/// <summary>A dialog came to the front: the page and its kind, so the window can bring a closing tab forward and give the dialog the keyboard.</summary>
	public event Action<object, string>? Showing;

	public void Open(object page, string kind, string pageUrl, string message, string? defaultText, IScriptDialogAnswer answer)
	{
		if (!ScriptDialogKinds.All.Contains(kind)) throw new ArgumentException($"Not a script dialog: {kind}", nameof(kind));
		_queue.Add(new Pending(_nextId++, page, kind, PermissionBroker.OriginOf(pageUrl), message, kind == ScriptDialogKinds.Prompt ? defaultText ?? "" : null, answer));
		ShowNext();
	}

	/// <summary>`dialog:answer`. An id no longer queued (its page went first) is ignored.</summary>
	public void Answer(int id, bool accept, string? text)
	{
		var at = _queue.FindIndex(p => p.Id == id);
		if (at == -1) return;
		var pending = _queue[at];
		_queue.RemoveAt(at);
		if (accept) pending.Answer.Accept(text);
		else pending.Answer.Dismiss();
		ShowNext();
	}

	/// <summary>Whether the page has a "Leave site?" prompt waiting for the person, shown or queued.</summary>
	public bool IsAsking(object page) => _queue.Any(p => ReferenceEquals(p.Page, page) && p.Kind == ScriptDialogKinds.BeforeUnload);

	/// <summary>Answers the page's "Leave site?" prompt with Stay: the close it was for was called off.</summary>
	public void Stay(object page) => Dismiss(p => ReferenceEquals(p.Page, page) && p.Kind == ScriptDialogKinds.BeforeUnload);

	/// <summary>The page closed or went to a new document: its dialogs went with it.</summary>
	public void DropFor(object page) => Dismiss(p => ReferenceEquals(p.Page, page));

	/// <summary>Call after the tabs change: a dialog names its tab only while that tab isn't the one shown.</summary>
	public void Refresh() => ShowNext();

	private void Dismiss(Func<Pending, bool> which)
	{
		var dropped = _queue.Where(which).ToList();
		if (dropped.Count == 0) return;
		_queue.RemoveAll(p => dropped.Contains(p));
		foreach (var pending in dropped) pending.Answer.Dismiss();
		ShowNext();
	}

	private void ShowNext()
	{
		var next = _queue.FirstOrDefault();
		if (next is not null && _announced != next.Id)
		{
			_announced = next.Id;
			Showing?.Invoke(next.Page, next.Kind);
		}
		// Showing may have brought the page's tab forward, which refreshed the prompt already.
		next = _queue.FirstOrDefault();
		var prompt = next is null ? null : new ScriptDialogPrompt(next.Id, next.Kind, next.Origin, next.Message, next.DefaultText, hiddenTabOf(next.Page));
		if (ui.State.Dialog == prompt) return;
		ui.Update(s => s with { Dialog = prompt });
	}
}
