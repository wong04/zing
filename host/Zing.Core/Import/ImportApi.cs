using Zing.Core.Engines;

namespace Zing.Core.Import;

/// <summary>What the renderer may know of a source: never its path.</summary>
public sealed record ImportSourceView(string Id, string Browser, string Display, int? Bookmarks, string? Error, bool Passwords);

public sealed record BookmarkImportOutcome(string FolderTitle, int AddedToBar, int AddedToFolder, IReadOnlyList<string> Skipped);

public sealed record PasswordExportFound(string Token, string Name);

public sealed record WatchTimedOut();

public sealed class UnknownImportSourceException(string id) : ArgumentException($"{id} isn't an import source on this PC");

/// <summary>A source or file that couldn't be read; the message never carries a path.</summary>
public sealed class ImportReadException(string message, Exception inner) : IOException(message, inner);

/// <summary>The import:* channels: the browsers Zing can import from, and the merge of one of their bookmark sets (or a picked file) into the library.</summary>
public sealed class ImportApi(LibraryStore library, FileGrants grants, Func<string> localAppData, Func<IBrowserLauncher> launcher, Func<IFileTrash> trash, IScheduler scheduler, string downloadsDir, Action<string, object?> push) : IDisposable
{
	private PasswordExportWatch? _watch;

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
		return Merge(Guarded(() => ImportFile.ParseBookmarks(path), "this file"), Path.GetFileName(path));
	}

	/// <summary>Opens the source browser's password export page and waits for the CSV to land in the downloads folder; a new watch ends the old.</summary>
	/// <exception cref="UnknownImportSourceException"/>
	/// <exception cref="NoPasswordExportException"/>
	/// <exception cref="DownloadsFolderMissingException"/>
	/// <exception cref="BrowserNotInstalledException"/>
	public void WatchPasswords(string id)
	{
		var source = ImportSources.Find(localAppData()).FirstOrDefault(s => s.Id == id) ?? throw new UnknownImportSourceException(id);
		var url = ExportPages.Url(source.Browser);
		var openLauncher = launcher();
		CancelWatch();
		var watch = new PasswordExportWatch(scheduler, downloadsDir, () => DateTimeOffset.UtcNow, Found, TimedOut);
		watch.Start();
		_watch = watch;
		try
		{
			openLauncher.Open(source.Browser, url);
		}
		catch
		{
			CancelWatch();
			throw;
		}
	}

	/// <summary>Cancelling when nothing is watched does nothing.</summary>
	public void CancelWatch()
	{
		_watch?.Stop();
		_watch = null;
	}

	/// <exception cref="UnknownFileGrantException"/>
	public void Recycle(string token) => trash().Recycle(grants.Resolve(token));

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
		new(source.Id, source.Browser.ToString().ToLowerInvariant(), source.Display, source.Bookmarks, source.Error, source.Browser != ImportBrowser.Arc);
}
