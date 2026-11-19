using System.Net;
using Zing.Core.Net;

namespace Zing.Core;

/// <summary>A dragged page image Zing won't take; the message is said to the person as it is.</summary>
public sealed class PageImageRefusedException(string message) : Exception(message);

/// <summary>
/// An image dragged out of a web page onto a tool. Chromium hands the drop zone the image's address, never a file, so the
/// host fetches it (<see cref="DropRouter"/>'s download plan) into a folder of its own under the viewer's temporary folder.
/// Each drop gets a folder named for this process, like the viewer's converted copies: they go when the window closes, and
/// the next start's sweep takes any a killed Zing left. The request carries no cookies or credentials, so an image only a
/// signed-in page can see is refused.
/// </summary>
/// <param name="onError">Called for a folder that couldn't be deleted yet (the next start's sweep takes it).</param>
public sealed class PageImageDrops(string root, HttpClient http, Action<string, Exception> onError) : IDisposable
{
	/// <summary>The formats taken: what <see cref="DropRouter.DetectExtension"/> recognises as a picture.</summary>
	private static readonly HashSet<string> Pictures = ["png", "jpg", "gif", "webp"];

	/// <summary>The one client every window fetches with: no cookies, a few redirects, and a page's patience.</summary>
	public static HttpClient Http { get; } = new(new SocketsHttpHandler
	{
		UseCookies = false,
		MaxAutomaticRedirections = 5,
		AutomaticDecompression = DecompressionMethods.All,
	})
	{
		Timeout = TimeSpan.FromSeconds(30),
	};

	private readonly object _gate = new();
	private readonly List<string> _folders = [];

	/// <summary>The fetched image's path. Only an http(s) address or a base64 <c>data:image/...</c> URL is fetched.</summary>
	/// <exception cref="PageImageRefusedException">Not such an address, the site refused, over <see cref="PinnedHttp.MaxBodyBytes"/>, or not a picture.</exception>
	public async Task<string> FetchAsync(string address, DateTimeOffset now, CancellationToken ct)
	{
		var (bytes, name) = address.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
			? FromDataUrl(address, now)
			: await DownloadAsync(address, ct);
		var picture = DropRouter.DetectExtension(bytes);
		if (picture is null || !Pictures.Contains(picture)) throw new PageImageRefusedException("That isn't a picture Zing can take: only PNG, JPEG, GIF and WebP images can be dragged in from a page.");
		return Write(bytes, WithExtension(name, picture));
	}

	private async Task<(byte[] Bytes, string Name)> DownloadAsync(string address, CancellationToken ct)
	{
		if (DropRouter.PlanDrop(new DropPayload(Url: address), DateTimeOffset.UnixEpoch) is not DropPlan.Download plan)
			throw new PageImageRefusedException("Only a picture from a web page (an http or https address) can be dragged in.");
		byte[] bytes;
		try
		{
			using var response = await http.GetAsync(plan.Url, HttpCompletionOption.ResponseHeadersRead, ct);
			if (!response.IsSuccessStatusCode) throw new PageImageRefusedException($"The site wouldn't hand over that picture (HTTP {(int)response.StatusCode}). Save it from the page, then drop the file here.");
			bytes = await BoundedBody.ReadAsync(response.Content, ct);
		}
		catch (BodyTooLargeError)
		{
			throw TooLarge();
		}
		catch (TaskCanceledException) when (!ct.IsCancellationRequested)
		{
			throw new PageImageRefusedException("The site took too long to hand over that picture. Save it from the page, then drop the file here.");
		}
		catch (HttpRequestException error)
		{
			throw new PageImageRefusedException($"Zing couldn't fetch that picture: {error.Message}");
		}
		return (bytes, DropRouter.NameForDownload(plan.Url, null, bytes));
	}

	private static (byte[] Bytes, string Name) FromDataUrl(string address, DateTimeOffset now)
	{
		const string marker = ";base64,";
		var at = address.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
		if (at < 0 || !address[5..at].StartsWith("image/", StringComparison.OrdinalIgnoreCase)) throw new PageImageRefusedException("That isn't a picture Zing can take: only PNG, JPEG, GIF and WebP images can be dragged in from a page.");
		var base64 = address[(at + marker.Length)..];
		// Base64 is 4 characters for every 3 bytes, so a longer string can't decode to the limit or less.
		if (base64.Length > (PinnedHttp.MaxBodyBytes + 2) / 3 * 4) throw TooLarge();
		try
		{
			var bytes = Convert.FromBase64String(base64);
			return (bytes, DropRouter.NameForDroppedImage(bytes, null, now));
		}
		catch (FormatException)
		{
			throw new PageImageRefusedException("That picture's data is damaged, so Zing can't take it.");
		}
	}

	private static PageImageRefusedException TooLarge() =>
		new($"That picture is over {PinnedHttp.MaxBodyBytes / (1024 * 1024)} MB, more than Zing fetches from a page. Save it from the page, then drop the file here.");

	/// <summary>The name with the extension of what the bytes are, since a URL's name ("photo.php", "img") can say otherwise.</summary>
	private static string WithExtension(string name, string picture)
	{
		var ext = DropRouter.PathExtension(name).ToLowerInvariant();
		if (ext == picture || (picture == "jpg" && ext == "jpeg")) return name;
		var stem = ext == "" ? name : name[..^(ext.Length + 1)];
		return $"{stem}.{picture}";
	}

	private string Write(byte[] bytes, string name)
	{
		var folder = Path.Combine(root, $"{Environment.ProcessId}_drop{Guid.NewGuid():N}");
		Directory.CreateDirectory(folder);
		lock (_gate) _folders.Add(folder);
		var path = Path.Combine(folder, name);
		File.WriteAllBytes(path, bytes);
		return path;
	}

	/// <summary>Deletes every fetched image. One a reader still holds is reported and left for the next start's sweep (ViewerFiles.SweepLeftovers).</summary>
	public void Dispose()
	{
		List<string> folders;
		lock (_gate)
		{
			folders = [.. _folders];
			_folders.Clear();
		}
		foreach (var folder in folders)
		{
			try
			{
				if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
			}
			catch (Exception error) when (error is IOException or UnauthorizedAccessException)
			{
				onError($"delete {folder}", error);
			}
		}
	}
}
