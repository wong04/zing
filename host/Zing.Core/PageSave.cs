using System.Text;
using System.Text.Json;

namespace Zing.Core;

/// <summary>The page can't be saved as it is; the reason is for the person.</summary>
public sealed class PageNotSavableException(string reason) : InvalidOperationException(reason);

/// <summary>The HTML of a page and the title the tab had when it was asked.</summary>
public sealed record PageText(string Title, string Html);

/// <summary>
/// Saving the current tab's HTML (X9). The text comes in as a parameter, so the direct route (the page answers a script)
/// and a later relay route (the HTML fetched elsewhere) share the one writer. Only what is on the page: no images, styles
/// or scripts are fetched or bundled.
/// </summary>
public static class PageSave
{
	/// <summary>Larger pages are refused rather than half-written.</summary>
	public const int MaxBytes = 64 * 1024 * 1024;

	/// <summary>How long a page may take to give up its text; a hung or crashed render process never answers.</summary>
	public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

	private const int MaxNameLength = 80;
	private const int MaxNumbering = 100;

	/// <summary>The document type (which outerHTML leaves out) followed by the root element, as the page holds them now.</summary>
	public const string Script = "(() => { const d = document; const type = d.doctype ? new XMLSerializer().serializeToString(d.doctype) : ''; return type + d.documentElement.outerHTML })()";

	/// <exception cref="PageNotSavableException">Not a web page, in reader mode, the page didn't answer in time or at all, it moved to another page meanwhile, or it didn't answer with text.</exception>
	public static async Task<PageText> ReadAsync(IPage page, TimeSpan? timeout = null)
	{
		var url = WithoutFragment(page.Url);
		var title = page.Title;
		if (!IsWeb(url)) throw new PageNotSavableException("Only web pages can be saved.");
		if (page.InReader) throw new PageNotSavableException("Leave reader mode first; this copy would be the article, not the page.");
		JsonElement answer;
		try
		{
			answer = await page.EvaluateAsync(Script).WaitAsync(timeout ?? DefaultTimeout);
		}
		catch (TimeoutException)
		{
			throw new PageNotSavableException("The page didn't answer in time. It may have stopped responding.");
		}
		// What a dead or navigating view raises: the page is not running, WebView2's own failure, or a result that isn't JSON.
		// Anything else (a null, a disposed object) is a bug and surfaces as one.
		catch (Exception error) when (error is (InvalidOperationException and not ObjectDisposedException) or System.Runtime.InteropServices.COMException or JsonException)
		{
			throw new PageNotSavableException($"The page couldn't be read ({error.GetType().Name}). It may have crashed; reload it and try again.");
		}
		// The text belongs to the page the person asked about; if the tab moved on meanwhile it is another page's.
		var now = WithoutFragment(page.Url);
		if (now != url || !IsWeb(now)) throw new PageNotSavableException("The page changed while it was being saved. Try again once it has loaded.");
		return answer.ValueKind == JsonValueKind.String ? new PageText(title, answer.GetString()!) : throw new PageNotSavableException("The page didn't give up its text.");
	}

	private static string WithoutFragment(string url) => url.Split('#')[0];

	private static bool IsWeb(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

	/// <summary>
	/// Writes the HTML as UTF-8 with a byte-order mark (so a meta charset in the page can't make the copy read as another
	/// encoding) under "title.html", numbered rather than overwriting, and returns its path. The text goes to a hidden part
	/// file first, so a failure never leaves a half-written page under a real name.
	/// </summary>
	/// <exception cref="PageNotSavableException">Over <see cref="MaxBytes"/>.</exception>
	public static string Write(string directory, string title, string html)
	{
		var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble().Concat(Encoding.UTF8.GetBytes(html)).ToArray();
		if (bytes.Length > MaxBytes) throw new PageNotSavableException("This page is too large to save.");
		Directory.CreateDirectory(directory);
		var part = Path.Combine(directory, $".{Guid.NewGuid():N}.zing-part");
		try
		{
			File.WriteAllBytes(part, bytes);
			return MoveIntoPlace(part, directory, BaseName(title) + ".html");
		}
		finally
		{
			if (File.Exists(part)) File.Delete(part);
		}
	}

	private static string BaseName(string title)
	{
		var trimmed = title.Trim();
		if (trimmed.Length == 0) return "page";
		if (trimmed.Length <= MaxNameLength) return trimmed;
		// Not between the two halves of a surrogate pair, which would leave an invalid name.
		var end = char.IsHighSurrogate(trimmed[MaxNameLength - 1]) ? MaxNameLength - 1 : MaxNameLength;
		return trimmed[..end];
	}

	internal static string MoveIntoPlace(string part, string directory, string name)
	{
		// File.Move refuses to overwrite, so a file that arrives after the name was chosen makes the next number be tried.
		for (var attempt = 0; attempt < MaxNumbering; attempt++)
		{
			var target = Path.Combine(directory, DownloadNames.FileName(name, candidate => File.Exists(Path.Combine(directory, candidate))));
			try
			{
				File.Move(part, target, overwrite: false);
				return target;
			}
			catch (IOException) when (File.Exists(target))
			{
				// Taken in the instant since it was checked; choose again.
			}
		}
		throw new IOException($"Couldn't find a free name for {name} in {directory}");
	}
}
