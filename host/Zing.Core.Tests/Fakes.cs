using Zing.Core;

namespace Zing.Core.Tests;

public sealed class FakePage(IPageEvents events) : IPage
{
	public IPageEvents Events { get; } = events;
	public List<string> Calls { get; } = [];
	public string Url { get; set; } = "";
	public string Title { get; set; } = "";
	public bool IsLoading { get; set; }
	public bool CanGoBack { get; set; }
	public bool CanGoForward { get; set; }
	public bool Shown { get; set; }
	public bool Closed { get; private set; }

	public void Navigate(string url)
	{
		Calls.Add($"navigate {url}");
		Url = url;
	}

	public void GoBack() => Calls.Add("back");
	public void GoForward() => Calls.Add("forward");
	public void Reload() => Calls.Add("reload");
	public void Stop() => Calls.Add("stop");
	public void Focus() => Calls.Add("focus");
	public void Run(string script) => Calls.Add("run");
	public void Find(string query, FindDirection direction, FindMode mode) => Calls.Add($"find {query} {direction} {mode}");
	public void StopFind() => Calls.Add("stop find");
	public void Close() => Closed = true;
}

public sealed class FakePageHost : IPageHost
{
	public List<FakePage> Created { get; } = [];

	public FakePage? Shown => Created.SingleOrDefault(p => p.Shown);

	public IPage Create(IPageEvents events)
	{
		var page = new FakePage(events);
		Created.Add(page);
		return page;
	}

	public void Show(IPage page)
	{
		foreach (var other in Created) other.Shown = false;
		((FakePage)page).Shown = true;
	}

	public void Hide(IPage page) => ((FakePage)page).Shown = false;
}

public sealed class FakeWindowHost : IWindowHost
{
	public FakePageHost FakePages { get; } = new();
	public IPageHost Pages => FakePages;
	public List<(string Channel, object? Payload)> Pushed { get; } = [];
	public List<string> Notices { get; } = [];
	public int NewWindows { get; private set; }
	public int Closes { get; private set; }
	public int Relayouts { get; private set; }

	public void Push(string channel, object? payload) => Pushed.Add((channel, payload));
	public void Notify(string text) => Notices.Add(text);
	public void NewWindow() => NewWindows++;
	public void CloseWindow() => Closes++;
	public void Relayout() => Relayouts++;
	public void FocusChrome() { }
	public void FocusSurface() { }

	public List<IReadOnlyList<MenuItem>> Menus { get; } = [];
	public void ShowMenu(IReadOnlyList<MenuItem> items, double x, double y) => Menus.Add(items);

	public List<PageUiState> PageUiStates { get; } = [];
	public void PageUiChanged(PageUiState state) => PageUiStates.Add(state);
	public void SetPageUiHeight(int height) { }
	public void FocusPageUi() { }

	public string DownloadsDirectory => Path.GetTempPath();
	public IDownloadHost DownloadHost { get; } = new FakeDownloads();

	public ManualScheduler FakeScheduler { get; } = new();
	public IScheduler Scheduler => FakeScheduler;

	public FakeShell FakeShell { get; } = new();
	public IShellActions Shell => FakeShell;
}

public sealed class FakeDownloads : IDownloadHost
{
	public bool FileExists(string path) => false;
	public string? OpenFile(string path) => null;
	public void ShowInFolder(string path) { }
	public void Cancel(int id) { }
}

public sealed class FakeShell : IShellActions
{
	public List<string> Copied { get; } = [];
	public List<string> Opened { get; } = [];
	public void CopyText(string text) => Copied.Add(text);
	public void OpenExternal(string url) => Opened.Add(url);
}

/// <summary>Ids t1, t2…, and a clock that ticks once per read, as in the shared tab-store vectors.</summary>
public static class Deterministic
{
	public static Func<string> Ids(string prefix = "t")
	{
		var next = 0;
		return () => $"{prefix}{++next}";
	}

	public static Func<long> Clock(long start = 1000)
	{
		var time = start;
		return () => time++;
	}
}
