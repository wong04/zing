using Zing.Core.Engines;

namespace Zing.Core.Import;

/// <summary>What the renderer may know of a source: never its path.</summary>
public sealed record ImportSourceView(string Id, string Browser, string Display, int? Bookmarks, string? Error, bool Passwords);

public sealed record BookmarkImportOutcome(string FolderTitle, int AddedToBar, int AddedToFolder, IReadOnlyList<string> Skipped);

public sealed class UnknownImportSourceException(string id) : ArgumentException($"{id} isn't an import source on this PC");

/// <summary>A source or file that couldn't be read; the message never carries a path.</summary>
public sealed class ImportReadException(string message, Exception inner) : IOException(message, inner);

/// <summary>The import:* channels: the browsers Zing can import from, and the merge of one of their bookmark sets (or a picked file) into the library.</summary>
public sealed class ImportApi(LibraryStore library, FileGrants grants, Func<string> localAppData)
{
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
