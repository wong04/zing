using System.Text;
using System.Text.Json;

namespace Zing.Core;

/// <summary>The page can't be saved as it is; the reason is for the person.</summary>
public sealed class PageNotSavableException(string reason) : InvalidOperationException(reason);

/// <summary>
/// Saving the current tab's HTML (X9). The text comes in as a parameter, so the direct route (the page answers a script)
/// and a later relay route (the HTML fetched elsewhere) share the one writer. Only what is on the page: no images, styles
/// or scripts are fetched or bundled.
/// </summary>
public static class PageSave
{
	/// <summary>Larger pages are refused rather than half-written.</summary>
	public const int MaxBytes = 64 * 1024 * 1024;

	private const int MaxNameLength = 80;
	private const int MaxNumbering = 100;

	/// <summary>The document type (which outerHTML leaves out) followed by the root element, as the page holds them now.</summary>
	public const string Script = "(() => { const d = document; const type = d.doctype ? new XMLSerializer().serializeToString(d.doctype) : ''; return type + d.documentElement.outerHTML })()";

	/// <exception cref="PageNotSavableException">Not a web page, in reader mode, or the page didn't answer with text.</exception>
	public static async Task<string> ReadAsync(IPage page)
	{
		if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new PageNotSavableException("Only web pages can be saved.");
		if (page.InReader) throw new PageNotSavableException("Leave reader mode first; this copy would be the article, not the page.");
		var answer = await page.EvaluateAsync(Script);
		return answer.ValueKind == JsonValueKind.String ? answer.GetString()! : throw new PageNotSavableException("The page didn't give up its text.");
	}

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
		var shortened = trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
		return trimmed.Length == 0 ? "page" : shortened;
	}

	private static string MoveIntoPlace(string part, string directory, string name)
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
