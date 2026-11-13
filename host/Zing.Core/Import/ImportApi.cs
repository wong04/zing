using System.Security;
using Zing.Core.Engines;

namespace Zing.Core.Import;

/// <summary>What the renderer may know of a source: never its path.</summary>
public sealed record ImportSourceView(string Id, string Browser, string Display, int? Bookmarks, string? Error, bool Passwords);

public sealed record BookmarkImportOutcome(string FolderTitle, int AddedToBar, int AddedToFolder, IReadOnlyList<string> Skipped);

public sealed record PasswordExportFound(string Token, string Name);

public sealed record WatchTimedOut();

public sealed class NotAPasswordExportException() : InvalidOperationException("That file isn't a password export, so Zing won't delete it");

public sealed class UnknownImportSourceException(string id) : ArgumentException($"{id} isn't an import source on this PC");

/// <summary>A source or file that couldn't be read; the message never carries a path.</summary>
public sealed class ImportReadException(string message, Exception inner) : IOException(message, inner);

/// <summary>What the New Tab card needs from the host: the setting, the default browser, Settings and the open start pages, and a place to report a scan nobody awaits (null in tests, which await the scan).</summary>
public sealed record NudgeHost(Func<bool> IsDone, Action MarkDone, Func<string?> HttpsProgId, Action<string> OpenSettings, Action ReloadStartPages, Action<Task, string>? Background = null);

/// <summary>The password import the New Tab card asked Settings to start.</summary>
public sealed record PendingImport(string Action, string SourceId);

/// <summary>The import:* channels: the browsers Zing can import from, and the merge of one of their bookmark sets (or a picked file) into the library.</summary>
public sealed class ImportApi(LibraryStore library, FileGrants grants, Func<string> localAppData, Func<IBrowserLauncher> launcher, Func<IFileTrash> trash, IScheduler scheduler, string downloadsDir, Action<string, object?> push, NudgeHost? nudgeHost = null) : IDisposable
{
	private PasswordExportWatch? _watch;
	// The card state lives here, in memory: Settings opens in the same window, and nothing about it may reach the disk.
	private ImportNudge? _cached;
	private ImportNudge.Result? _result;
	// The profile scan behind the card, off the UI thread. An answer to the card meanwhile moves the epoch on, so a late scan drops its card.
	private Task? _scan;
	private int _epoch;
	private PendingImport? _pending;

	public void Dispose() => CancelWatch();

	public IReadOnlyList<ImportSourceView> Sources() => [.. ImportSources.Find(localAppData()).Select(View)];

	/// <exception cref="UnknownImportSourceException"/>
	/// <exception cref="ImportReadException">The source's file couldn't be read.</exception>
	/// <exception cref="BookmarksFileDamagedException"/>
	/// <exception cref="ChromiumBookmarksFormatException"/>
	/// <exception cref="ArcSidebarFormatException"/>
	/// <exception cref="ImportFileTooLargeException"/>
	/// <exception cref="ImportFileNotUtf8Exception"/>
	public BookmarkImportOutcome ImportSource(string id)
	{
		var outcome = ImportSourceCore(id);
		EndNudge();
		return outcome;
	}

	private BookmarkImportOutcome ImportSourceCore(string id)
	{
		var source = ImportSources.Find(localAppData()).FirstOrDefault(s => s.Id == id) ?? throw new UnknownImportSourceException(id);
		return Merge(Guarded(() => ImportSources.Read(source), "this browser's bookmarks"), source.Display);
	}

	/// <exception cref="UnknownFileGrantException"/>
	/// <exception cref="ImportReadException">The file couldn't be read.</exception>
	/// <exception cref="UnknownBookmarksFileException"/>
	/// <exception cref="BookmarksFileDamagedException"/>
	/// <exception cref="ChromiumBookmarksFormatException"/>
	/// <exception cref="ArcSidebarFormatException"/>
	/// <exception cref="ImportFileTooLargeException"/>
	/// <exception cref="ImportFileNotUtf8Exception"/>
	public BookmarkImportOutcome ImportPicked(string token)
	{
		var path = grants.Resolve(token);
		var outcome = Merge(Guarded(() => ImportFile.ParseBookmarks(path), "this file"), Path.GetFileName(path));
		EndNudge();
		return outcome;
	}

	/// <summary>An import from Settings answers the card too: it stays away from now on.</summary>
	private void EndNudge()
	{
		if (nudgeHost is not { } host || host.IsDone()) return;
		_epoch++;
		_cached = null;
		host.MarkDone();
		host.ReloadStartPages();
	}

	/// <summary>Opens the source browser's password export page and waits for the CSV to land in the downloads folder; a new watch ends the old.</summary>
	/// <exception cref="UnknownImportSourceException"/>
	/// <exception cref="NoPasswordExportException"/>
	/// <exception cref="DownloadsFolderMissingException"/>
	/// <exception cref="BrowserNotInstalledException"/>
	/// <exception cref="NoProfileToOpenException"/>
	public void WatchPasswords(string id)
	{
		var source = ImportSources.Find(localAppData()).FirstOrDefault(s => s.Id == id) ?? throw new UnknownImportSourceException(id);
		var url = ExportPages.Url(source.Browser);
		var profile = source.Profile ?? throw new NoProfileToOpenException(source.Browser);
		var openLauncher = launcher();
		CancelWatch();
		var watch = new PasswordExportWatch(scheduler, downloadsDir, () => DateTimeOffset.UtcNow, Found, TimedOut);
		watch.Start();
		_watch = watch;
		try
		{
			openLauncher.Open(source.Browser, profile, url);
		}
		catch
		{
			CancelWatch();
			throw;
		}
	}

	/// <summary>
	/// The card for the start page: a result while one is held, nothing once answered, else an offer from the default browser or the generic ask.
	/// The disk scan for an offer runs off the UI thread, so until it ends this is null and the page is served without a card; the start pages reload when the card is ready.
	/// Cached per launch, so New Tab doesn't scan the disk; a failed scan is cached too (as the generic card with its error), or the reload would start another.
	/// </summary>
	public ImportNudge? Nudge()
	{
		if (_result is not null) return _result;
		var host = RequireNudgeHost();
		if (host.IsDone()) return null;
		if (_cached is not null) return _cached;
		if (_scan is { IsCompleted: false }) return null;
		var progId = host.HttpsProgId();
		// Zing, Firefox or nothing as the default can't be a source, so no profile is worth parsing.
		if (DefaultBrowserSource.BrowserFor(progId) is null) return _cached = new ImportNudge.Generic();
		_scan = ScanAsync(host, progId);
		host.Background?.Invoke(_scan, "import nudge scan");
		return null;
	}

	/// <summary>Ends when the card for the current scan, if any, has been set and the start pages told. Tests await it.</summary>
	public Task Scanned => _scan ?? Task.CompletedTask;

	// Resumes on the UI thread (the caller's context), where the card state lives.
	private async Task ScanAsync(NudgeHost host, string? progId)
	{
		var epoch = _epoch;
		ImportNudge card;
		try
		{
			card = await Task.Run(() => Offer(progId));
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
		{
			card = new ImportNudge.Generic(ImportErrors.Explain(error, "the browsers on this PC"));
		}
		// Answered, or imported from Settings, while the scan ran: its card is stale.
		if (epoch != _epoch || host.IsDone() || _result is not null) return;
		_cached = card;
		host.ReloadStartPages();
	}

	/// <summary>Runs a button of the card. Navigation to the card's links never loads a page.</summary>
	public void Act(string action, string? sourceId)
	{
		var host = RequireNudgeHost();
		switch (action)
		{
			case "bookmarks":
				// A double click or a stale New Tab: the card already shows (or will show) the right state.
				if (_result is not null || host.IsDone()) return;
				ImportFromCard(host, sourceId ?? throw new ArgumentException("The bookmarks action needs a source"));
				return;
			case "settings":
				host.OpenSettings("import");
				return;
			case "passwords":
				_pending = new PendingImport("passwords", sourceId ?? throw new ArgumentException("The passwords action needs a source"));
				_result = null;
				host.ReloadStartPages();
				host.OpenSettings("import");
				return;
			case "dismiss":
				_epoch++;
				_result = null;
				_cached = null;
				host.MarkDone();
				host.ReloadStartPages();
				return;
			default:
				throw new ArgumentException($"{action} isn't an import action");
		}
	}

	/// <summary>The password import waiting for Settings, once.</summary>
	public PendingImport? TakePending()
	{
		var pending = _pending;
		_pending = null;
		return pending;
	}

	/// <summary>Cancelling when nothing is watched does nothing.</summary>
	public void CancelWatch()
	{
		_watch?.Stop();
		_watch = null;
	}

	/// <exception cref="UnknownFileGrantException"/>
	/// <exception cref="NotAPasswordExportException">The file isn't a password export, so it isn't Zing's to delete.</exception>
	public void Recycle(string token)
	{
		var path = grants.Resolve(token);
		if (!PasswordExportWatch.IsPasswordExport(path)) throw new NotAPasswordExportException();
		trash().Recycle(path);
	}

	private NudgeHost RequireNudgeHost() => nudgeHost ?? throw new InvalidOperationException("This host has no New Tab import card");

	// Runs off the UI thread: it reads other browsers' profiles.
	private ImportNudge Offer(string? progId)
	{
		if (DefaultBrowserSource.Pick(progId, ImportSources.Find(localAppData())) is not { } source) return new ImportNudge.Generic();
		return new ImportNudge.Offer(Label(source), ProfileOf(source), source.Bookmarks ?? 0, source.Id, ExportPages.Has(source.Browser));
	}

	private void ImportFromCard(NudgeHost host, string id)
	{
		_epoch++;
		_cached = null;
		try
		{
			var source = ImportSources.Find(localAppData()).FirstOrDefault(s => s.Id == id) ?? throw new UnknownImportSourceException(id);
			var outcome = ImportSourceCore(id);
			_result = new ImportNudge.Result(Label(source), outcome.AddedToBar + outcome.AddedToFolder, source.Id, ExportPages.Has(source.Browser));
			host.MarkDone();
		}
		catch (Exception error) when (error is UnknownImportSourceException or IOException or UnauthorizedAccessException or SecurityException or BookmarksFileDamagedException or ChromiumBookmarksFormatException or ArcSidebarFormatException or ImportFileTooLargeException or ImportFileNotUtf8Exception)
		{
			// The card shows why; the card then offers Choose… and Not now.
			_cached = new ImportNudge.Generic(error.Message);
		}
		host.ReloadStartPages();
	}

	// Display is "Chrome (Personal)" or "Arc".
	private static string Label(ImportSource source) => source.Display.Split(" (")[0];

	private static string? ProfileOf(ImportSource source)
	{
		var open = source.Display.IndexOf(" (", StringComparison.Ordinal);
		return open < 0 || !source.Display.EndsWith(')') ? null : source.Display[(open + 2)..^1];
	}

	private void Found(string path)
	{
		_watch = null;
		var grant = grants.Issue(path);
		push("import:password-export", new PasswordExportFound(grant.Token, grant.Name));
	}

	private void TimedOut()
	{
		_watch = null;
		push("import:watch-timed-out", new WatchTimedOut());
	}

	private BookmarkImportOutcome Merge(BookmarkImport import, string source)
	{
		var outcome = library.Import(import, source);
		return new BookmarkImportOutcome(outcome.FolderTitle, outcome.AddedToBar, outcome.AddedToFolder, import.Skipped);
	}

	private static BookmarkImport Guarded(Func<BookmarkImport> read, string what)
	{
		try
		{
			return read();
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			throw new ImportReadException(ImportErrors.Explain(error, what), error);
		}
	}

	private static ImportSourceView View(ImportSource source) =>
		new(source.Id, source.Browser.ToString().ToLowerInvariant(), source.Display, source.Bookmarks, source.Error, ExportPages.Has(source.Browser));
}
