namespace Zing.Core;

/// <summary>Port of src/shared/browser/pageUi.ts: what the page-UI layer draws over the page.</summary>
public sealed record FindState(string Query, int Active, int Matches);

public static class DownloadStatuses
{
	public const string Progressing = "progressing";
	public const string Completed = "completed";
	public const string Cancelled = "cancelled";
	public const string Failed = "failed";
}

/// <param name="Total">0 when the server sent no length.</param>
public sealed record DownloadEntry(int Id, string Filename, string Status, long Received, long Total, string? Error = null);

public static class PermissionKinds
{
	public const string Camera = "camera";
	public const string Microphone = "microphone";
	public const string CameraAndMicrophone = "camera and microphone";
	public const string Location = "location";
	public const string Notifications = "notifications";
}

public sealed record PermissionPrompt(int Id, string Origin, string Kind);

/// <summary>Serialises, with <see cref="ZingJson.Options"/>, exactly like the TS `PageUiState`.</summary>
public sealed record PageUiState(
	FindState? Find,
	int FindFocus,
	IReadOnlyList<DownloadEntry> Downloads,
	bool DownloadsOpen,
	PermissionPrompt? Permission)
{
	public static readonly PageUiState Empty = new(null, 0, [], false, null);

	[System.Text.Json.Serialization.JsonIgnore]
	public bool HasVisiblePageUi => Find is not null || Permission is not null || (DownloadsOpen && Downloads.Count > 0);
}

public enum FindDirection { Next, Previous }

public static class FindDirections
{
	public static FindDirection Parse(string text) => text switch
	{
		"next" => FindDirection.Next,
		"previous" => FindDirection.Previous,
		_ => throw new ArgumentException($"Not a find direction: {text}", nameof(text)),
	};
}

/// <summary>The one page-UI state of a window. Every change is pushed (`pageui:state`) to the renderer.</summary>
public sealed class PageUiStore
{
	public PageUiState State { get; private set; } = PageUiState.Empty;

	public event Action<PageUiState>? Changed;

	public void Update(Func<PageUiState, PageUiState> change)
	{
		State = change(State);
		Changed?.Invoke(State);
	}
}

public enum FindMode { Restart, Continue }

/// <summary>What <see cref="FindController"/> needs from the host. Pages are opaque handles.</summary>
public interface IFindHost
{
	object? ActivePage { get; }
	bool IsAlive(object page);
	void Search(object page, string query, FindDirection direction, FindMode mode);
	void ClearSelection(object page);
	void FocusPanel();
	void FocusPage(object page);
}

/// <summary>Port of the state in src/main/find.ts: the bar, its query and which page is being searched.</summary>
public sealed class FindController(PageUiStore ui, IFindHost host)
{
	private object? _target;
	private string _query = "";
	private int _focusToken;

	/// <summary>Call for each result the engine reports for <paramref name="page"/>.</summary>
	public void OnResult(object page, int matches, int activeOrdinal)
	{
		if (!ReferenceEquals(page, _target) || ui.State.Find is null) return;
		ui.Update(s => s with { Find = new FindState(_query, matches == 0 ? 0 : activeOrdinal, matches) });
	}

	public void Open()
	{
		var page = host.ActivePage;
		if (page is null) return;
		if (!ReferenceEquals(_target, page)) Stop();
		_target = page;
		var find = ui.State.Find ?? new FindState(_query, 0, 0);
		ui.Update(s => s with { Find = find, FindFocus = ++_focusToken });
		host.FocusPanel();
		if (_query != "") host.Search(page, _query, FindDirection.Next, FindMode.Restart);
	}

	/// <summary>Find Next / Previous with a remembered query; without one it opens the bar.</summary>
	public void Step(FindDirection direction)
	{
		if (_query == "" || ui.State.Find is null)
		{
			Open();
			return;
		}
		if (_target is null || !host.IsAlive(_target)) return;
		host.Search(_target, _query, direction, FindMode.Continue);
	}

	public void Update(string text)
	{
		_query = text;
		if (_target is null || !host.IsAlive(_target)) return;
		if (text == "")
		{
			host.ClearSelection(_target);
			ui.Update(s => s with { Find = new FindState("", 0, 0) });
			return;
		}
		host.Search(_target, text, FindDirection.Next, FindMode.Restart);
	}

	public void Close()
	{
		Stop();
		_target = null;
		ui.Update(s => s with { Find = null });
		var active = host.ActivePage;
		if (active is not null) host.FocusPage(active);
	}

	/// <summary>The tab changed under an open bar: the old page's highlights go with it.</summary>
	public void ActiveChanged()
	{
		if (ui.State.Find is null || ReferenceEquals(host.ActivePage, _target)) return;
		Close();
	}

	private void Stop()
	{
		if (_target is not null && host.IsAlive(_target)) host.ClearSelection(_target);
	}
}
