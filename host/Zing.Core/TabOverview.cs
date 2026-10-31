namespace Zing.Core;

public sealed record OverviewTab(string Id, string Title, string Url, string? Icon, string? Thumbnail, bool Active);

public sealed record OverviewState(IReadOnlyList<OverviewTab> Tabs);

/// <summary>Tab pictures in memory only, least recently used dropped first (thumbnails.ts).</summary>
public sealed class ThumbnailCache(int capacity = 24)
{
	private readonly LinkedList<(string Id, string Image)> _order = new();
	private readonly Dictionary<string, LinkedListNode<(string Id, string Image)>> _entries = new(StringComparer.Ordinal);

	public int Count => _entries.Count;

	public string? Get(string id)
	{
		if (!_entries.TryGetValue(id, out var node)) return null;
		_order.Remove(node);
		_order.AddLast(node);
		return node.Value.Image;
	}

	public void Set(string id, string image)
	{
		Delete(id);
		_entries[id] = _order.AddLast((id, image));
		while (_entries.Count > capacity && _order.First is { } oldest) Delete(oldest.Value.Id);
	}

	public void Delete(string id)
	{
		if (!_entries.Remove(id, out var node)) return;
		_order.Remove(node);
	}
}

/// <summary>
/// The grid of tab pictures (tab-overview.ts). Pictures are taken only when
/// the overview opens and as a page leaves the screen (a page nobody can see
/// produces no frames), never in the background.
/// </summary>
public sealed class TabOverview(TabService tabs, Func<TabsState> state, Action<OverviewState?> shown, Action<string, Exception> captureFailed)
{
	private static readonly TimeSpan CaptureTimeout = TimeSpan.FromMilliseconds(1500);

	private readonly ThumbnailCache _thumbnails = new();
	private readonly HashSet<Task> _leaving = [];

	public bool IsOpen { get; private set; }

	/// <summary>Null while closed, so a renderer that loads late can still ask.</summary>
	public OverviewState? Current => IsOpen ? State() : null;

	public OverviewState State()
	{
		var list = state();
		return new OverviewState([.. list.Tabs.Select(t => new OverviewTab(t.Id, t.Title, t.Url, t.Icon, _thumbnails.Get(t.Id), t.Id == list.ActiveId))]);
	}

	public string? Thumbnail(string id) => _thumbnails.Get(id);

	public async Task ToggleAsync()
	{
		if (IsOpen) Close();
		else await OpenAsync();
	}

	public async Task OpenAsync()
	{
		if (IsOpen) return;
		IsOpen = true;
		// Pictures of tabs that just left the screen may still be on their way.
		await Task.WhenAll([.. _leaving, CaptureActiveAsync()]);
		// Closed again while the pictures were being taken.
		if (IsOpen) shown(State());
	}

	public void Close()
	{
		if (!IsOpen) return;
		IsOpen = false;
		shown(null);
		if (tabs.ActiveId is { } id) tabs.FocusPage(id);
	}

	/// <summary>Tabs came or went while open: redraw from what is already captured.</summary>
	public void Refresh()
	{
		if (IsOpen) shown(State());
	}

	/// <summary>Just before a tab's page leaves the screen: the last chance for its picture.</summary>
	public void Leaving(string id, IPage page)
	{
		Task pending = null!;
		pending = CaptureAsync(id, page).ContinueWith(_ => _leaving.Remove(pending), SchedulerExtensions.FromCurrentSynchronizationContextOrDefault());
		_leaving.Add(pending);
	}

	public void Forget(string id) => _thumbnails.Delete(id);

	private Task CaptureActiveAsync() =>
		tabs.ActiveId is { } id && tabs.PageOf(id) is { } page ? CaptureAsync(id, page) : Task.CompletedTask;

	/// <summary>A tab without a picture just shows its initial, but the reason is worth knowing.</summary>
	private async Task CaptureAsync(string id, IPage page)
	{
		try
		{
			var capture = page.CaptureAsync();
			if (await Task.WhenAny(capture, Task.Delay(CaptureTimeout)) != capture) throw new TimeoutException($"Capturing tab {id} took longer than {CaptureTimeout.TotalMilliseconds} ms");
			if (await capture is { } image) _thumbnails.Set(id, image);
		}
		catch (Exception error)
		{
			captureFailed(id, error);
		}
	}
}

internal static class SchedulerExtensions
{
	/// <summary>The UI thread's scheduler where there is one (the app), the default one in tests.</summary>
	public static TaskScheduler FromCurrentSynchronizationContextOrDefault() =>
		SynchronizationContext.Current is null ? TaskScheduler.Default : TaskScheduler.FromCurrentSynchronizationContext();
}
