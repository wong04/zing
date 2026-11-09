using Zing.Core;

[assembly: Xunit.AssemblyFixture(typeof(Zing.Core.Tests.AccountsCleanup))]

namespace Zing.Core.Tests;

public sealed class FakePage(IPageEvents events) : IPage
{
	public IPageEvents Events { get; private set; } = events;
	public void Rebind(IPageEvents events) => Events = events;
	public List<string> Calls { get; } = [];
	public string Url { get; set; } = "";
	public string Title { get; set; } = "";
	public bool IsLoading { get; set; }
	public bool CanGoBack { get; set; }
	public bool CanGoForward { get; set; }
	public bool IsFullScreen { get; set; }
	public bool HasOpenDialog { get; set; }
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
	/// <summary>Every script <see cref="Run"/> was given, in order.</summary>
	public List<string> Ran { get; } = [];
	public void Run(string script)
	{
		Calls.Add("run");
		Ran.Add(script);
	}
	public void Find(string query, FindDirection direction, FindMode mode) => Calls.Add($"find {query} {direction} {mode}");
	public void StopFind() => Calls.Add("stop find");
	public bool IsAudible { get; set; }
	public System.Text.Json.JsonElement ProbeResult { get; set; } = System.Text.Json.JsonDocument.Parse("{\"scrollY\":0,\"dirty\":false}").RootElement;
	/// <summary>When set, answers <see cref="EvaluateAsync"/> instead of <see cref="ProbeResult"/>, so a test can navigate, hang or fail mid-script.</summary>
	public Func<Task<System.Text.Json.JsonElement>>? Evaluating { get; set; }
	/// <summary>Every script <see cref="EvaluateAsync"/> was given, in order.</summary>
	public List<string> Evaluated { get; } = [];
	public Task<System.Text.Json.JsonElement> EvaluateAsync(string script)
	{
		Evaluated.Add(script);
		return Evaluating?.Invoke() ?? Task.FromResult(ProbeResult);
	}
	public void RunWhenLoaded(string script) => Calls.Add($"when loaded {script}");
	public int TextSize { get; set; } = Zing.Core.TextSize.Default;
	public double Zoom { get; set; } = Zing.Core.Zoom.Default;
	public void Print() => Calls.Add("print");
	public string? Picture { get; set; } = "data:image/jpeg;base64,AA==";
	public Task<string?> CaptureAsync() => Task.FromResult(Picture);
	public string? ReaderRefusal { get; set; }
	public bool InReader { get; private set; }
	public PageHistory History { get; set; } = new([], 0);
	public Task<PageHistory> HistoryAsync() => Task.FromResult(History);
	public void GoToHistoryEntry(int entryId) => Calls.Add($"history {entryId}");
	public Task<string?> ShowReaderAsync()
	{
		InReader = ReaderRefusal is null;
		return Task.FromResult(ReaderRefusal);
	}
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

	public SplitLayout? ShownSplit { get; private set; }

	private FakePage? _focused;

	// As MainWindow does: Show hides only the page that had the screen, and ShowSplit hides nothing, so the tab
	// service must hide every other page itself.
	public void Show(IPage page)
	{
		if (_focused is not null && _focused != page) _focused.Shown = false;
		_focused = (FakePage)page;
		_focused.Shown = true;
		ShownSplit = null;
	}

	public void ShowSplit(IReadOnlyList<IPage> pages, SplitLayout layout, IPage focused)
	{
		foreach (var page in pages) ((FakePage)page).Shown = true;
		_focused = (FakePage)focused;
		ShownSplit = layout;
	}

	/// <summary>The page a tab has now (it must have one).</summary>
	public FakePage PageFor(TabService tabs, string id) => (FakePage)(tabs.PageOf(id) ?? throw new InvalidOperationException($"Tab {id} has no page"));

	public void Hide(IPage page)
	{
		((FakePage)page).Shown = false;
		if (_focused == page) _focused = null;
	}

	/// <summary>Every ShowPreview call's layers, bottom first; an empty one removed the preview.</summary>
	public List<IReadOnlyList<IPage>> Previews { get; } = [];
	public int PreviewFocuses { get; private set; }

	public void ShowPreview(IReadOnlyList<IPage> layers) => Previews.Add([.. layers]);
	public void FocusPreview() => PreviewFocuses++;
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

	// Engine jobs push from their own threads.
	public void Push(string channel, object? payload)
	{
		lock (Pushed) Pushed.Add((channel, payload));
	}

	/// <summary>What the file picker "returns"; the extensions it was asked for are kept.</summary>
	public IReadOnlyList<string> PickedPaths { get; set; } = [];
	public IReadOnlyList<string>? PickedExtensions { get; private set; }

	public Task<IReadOnlyList<string>> PickFilesAsync(IReadOnlyList<string> extensions)
	{
		PickedExtensions = extensions;
		return Task.FromResult(PickedPaths);
	}

	/// <summary>The paths of each copy to the clipboard.</summary>
	public List<IReadOnlyList<string>> CopiedFiles { get; } = [];
	public Exception? ClipboardFailure { get; set; }

	public void CopyFilesToClipboard(IReadOnlyList<string> paths)
	{
		if (ClipboardFailure is { } failure) throw failure;
		CopiedFiles.Add(paths);
	}
	/// <summary>The HTML text each print was given.</summary>
	public List<string> Printed { get; } = [];

	public Task PrintHtmlToPdfAsync(string html, string pdfPath, CancellationToken cancellation)
	{
		Printed.Add(html);
		File.WriteAllText(pdfPath, "%PDF-fake");
		return Task.CompletedTask;
	}
	public void Notify(string text) => Notices.Add(text);
	public List<string> Offers { get; } = [];
	public int OfferDismissals { get; private set; }
	public void OfferUnblock(string address) => Offers.Add(address);
	public void DismissOffer() => OfferDismissals++;
	public event Action? OfferEnded;
	public void EndOffer() => OfferEnded?.Invoke();
	public void NewWindow() => NewWindows++;
	public void CloseWindow() => Closes++;
	public void Relayout() => Relayouts++;
	public void FocusChrome() { }
	public void FocusSurface() { }

	public List<IReadOnlyList<MenuItem>> Menus { get; } = [];
	public void ShowMenu(IReadOnlyList<MenuItem> items, double x, double y) => Menus.Add(items);

	public ContentSize ContentSize { get; set; } = new(1280, 820);
	public Point ContentOrigin => new(0, 0);
	public Point? Cursor { get; set; }
	public bool HighContrast { get; set; }
	public List<Rect?> SurfaceRegions { get; } = [];
	public void SurfaceRegionChanged(Rect? region) => SurfaceRegions.Add(region);

	public List<StackDropdownState?> Dropdowns { get; } = [];
	public void StackDropdownChanged(StackDropdownState? state) => Dropdowns.Add(state);

	public List<OverviewState?> Overviews { get; } = [];
	public void OverviewChanged(OverviewState? state) => Overviews.Add(state);
	public void Warn(string what, Exception error) { }
	public void Background(Task task, string what) => task.GetAwaiter().GetResult();

	public List<Exception> Reported { get; } = [];
	public void Report(string what, Exception error) => Reported.Add(error);

	public List<PageUiState> PageUiStates { get; } = [];
	public void PageUiChanged(PageUiState state) => PageUiStates.Add(state);
	public List<int> PageUiHeights { get; } = [];
	public void SetPageUiHeight(int height) => PageUiHeights.Add(height);
	public void FocusPageUi() { }
	public List<Zing.Core.Vault.AccountPicker?> Pickers { get; } = [];
	public void AccountPickerChanged(Zing.Core.Vault.AccountPicker? picker) => Pickers.Add(picker);
	public void SetAccountPickerHeight(int height) { }

	public string DownloadsDirectory { get; set; } = Path.GetTempPath();
	public IDownloadHost DownloadHost { get; } = new FakeDownloads();

	public ManualScheduler FakeScheduler { get; } = new();
	public IScheduler Scheduler => FakeScheduler;

	public FakeSecretClipboard FakeSecret { get; } = new();
	public Zing.Core.Vault.ISecretClipboard SecretClipboard => FakeSecret;

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

public sealed class FakeBrowsingData : IBrowsingData
{
	public int Clears { get; private set; }
	public Exception? Failure { get; set; }

	public Task ClearAllAsync()
	{
		if (Failure is not null) return Task.FromException(Failure);
		Clears++;
		return Task.CompletedTask;
	}
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

/// <summary>The app clock for window tests: ticks once per read, like <see cref="Deterministic.Clock"/>.</summary>
public sealed class TickingClock : IClock
{
	private readonly Func<long> _time = Deterministic.Clock();

	public long Now() => _time();

	public IDisposable OnAdvance(Action listener) => new Nothing();

	private sealed class Nothing : IDisposable
	{
		public void Dispose() { }
	}
}

/// <summary>A signed-in guest account in a scratch folder, so window tests start past the gate.</summary>
public static class Accounts
{
	/// <summary>One folder for the whole run, holding every test's account folder; <see cref="AccountsCleanup"/> deletes it when the run ends.</summary>
	public static readonly string Root = Directory.CreateTempSubdirectory("zing-account-").FullName;

	public static string Folder() => Directory.CreateDirectory(Path.Combine(Root, Guid.NewGuid().ToString("N"))).FullName;

	public static AccountStore Guest() => new(Folder(), new FakeCipher(), startAsGuest: true);
}

public sealed class AccountsCleanup : IDisposable
{
	public void Dispose() => Directory.Delete(Accounts.Root, recursive: true);
}

/// <summary>The clipboard a password is copied to, in memory: the real one is never touched. Every write or clear is a change, as on Windows.</summary>
public sealed class FakeSecretClipboard : Zing.Core.Vault.ISecretClipboard
{
	public string? Text { get; private set; }
	public long SequenceNumber { get; private set; }
	public List<string> Writes { get; } = [];

	public long Write(string text)
	{
		Text = text;
		Writes.Add(text);
		return ++SequenceNumber;
	}

	/// <summary>Something else copied: the sequence number moves and the text is no longer ours.</summary>
	public void CopiedElsewhere(string text)
	{
		Text = text;
		SequenceNumber++;
	}

	/// <summary>Makes Clear fail as it does when another program holds the clipboard.</summary>
	public Exception? ClearFailure { get; set; }

	public void Clear()
	{
		if (ClearFailure is { } failure) throw failure;
		Text = null;
		SequenceNumber++;
	}
}
