using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zing.Core.Import;

public sealed class ImportFileTooLargeException() : Exception($"This file is too large to be an import. Zing reads files of up to {ImportFile.MaxBytes / (1024 * 1024)} MB, which is far more than any real export.");

public sealed class ImportFileNotUtf8Exception() : Exception("The file isn't UTF-8 text, so it can't be an export Zing reads.");

public sealed class UnknownBookmarksFileException(string fileName) : Exception($"{fileName} isn't a bookmarks file Zing recognises. Zing reads the Bookmarks file of Chrome, Edge, Brave and Dia, Arc's StorableSidebar.json, and the bookmarks HTML every browser exports.");

public sealed class BookmarksFileDamagedException(string fileName) : Exception($"{fileName} looks like a bookmarks file but is damaged or incomplete.");

/// <summary>Reads a file the user picked for import: capped, strict UTF-8, and for bookmarks identified by its content rather than its name.</summary>
public static partial class ImportFile
{
	/// <summary>A real export is some hundreds of kilobytes at most; a bigger file isn't one, and is refused before it is read.</summary>
	public const int MaxBytes = 10 * 1024 * 1024;

	[GeneratedRegex(@"^<!DOCTYPE\s+NETSCAPE-Bookmark-file-1>", RegexOptions.IgnoreCase | RegexOptions.ECMAScript)]
	private static partial Regex NetscapeDoctype();

	/// <remarks>File-system errors (<see cref="FileNotFoundException"/>, <see cref="UnauthorizedAccessException"/>, <see cref="IOException"/>) are not caught.</remarks>
	/// <exception cref="ImportFileTooLargeException">Over <see cref="MaxBytes"/>.</exception>
	/// <exception cref="ImportFileNotUtf8Exception">Not UTF-8 text.</exception>
	public static string ReadText(string path)
	{
		// A running browser holds its profile files open for writing; sharing like it does is what lets us read them.
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		if (stream.Length > MaxBytes) throw new ImportFileTooLargeException();
		var bytes = new byte[stream.Length];
		stream.ReadExactly(bytes);
		try
		{
			return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
		}
		catch (DecoderFallbackException)
		{
			throw new ImportFileNotUtf8Exception();
		}
	}

	/// <exception cref="UnknownBookmarksFileException">Neither a Chromium Bookmarks file, an Arc sidebar nor a bookmarks HTML export.</exception>
	/// <exception cref="ImportFileTooLargeException"/>
	/// <exception cref="ImportFileNotUtf8Exception"/>
	/// <exception cref="ChromiumBookmarksFormatException"/>
	/// <exception cref="ArcSidebarFormatException"/>
	/// <exception cref="BookmarksFileDamagedException">Malformed or too deeply nested JSON.</exception>
	public static BookmarkImport ParseBookmarks(string path)
	{
		var text = ReadText(path).TrimStart('﻿').TrimStart();
		if (text.StartsWith('{'))
		{
			var fileName = Path.GetFileName(path);
			try
			{
				using var document = JsonDocument.Parse(text, BookmarkImport.JsonOptions);
				var root = document.RootElement;
				if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("roots", out _)) return ChromiumBookmarks.Parse(root);
				if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("sidebar", out _)) return ArcSidebar.Parse(root);
			}
			catch (JsonException)
			{
				throw new BookmarksFileDamagedException(fileName);
			}
		}
		if (NetscapeDoctype().IsMatch(text)) return NetscapeBookmarks.Parse(text);
		throw new UnknownBookmarksFileException(Path.GetFileName(path));
	}
}
