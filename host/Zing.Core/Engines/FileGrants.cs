namespace Zing.Core.Engines;

/// <summary>A file the person picked or dropped, as the renderer sees it: a name to show and a token to hand back. The path stays in the host.</summary>
public sealed record FileGrant(string Token, string Name);

/// <summary>
/// The files a window may give an engine: only those the native picker or a drop on a Zing view issued. The renderer
/// holds tokens, so a buggy or compromised renderer can't name any other path on the disk. The oldest are forgotten
/// past <see cref="Limit"/>.
/// </summary>
public sealed class FileGrants
{
	public const int Limit = 1000;

	private readonly object _gate = new();
	private readonly Dictionary<string, string> _paths = new(StringComparer.Ordinal);
	private readonly Queue<string> _order = new();

	public FileGrant Issue(string path)
	{
		var full = EngineArguments.FilePath(path);
		var token = Guid.NewGuid().ToString("N");
		lock (_gate)
		{
			_paths[token] = full;
			_order.Enqueue(token);
			while (_order.Count > Limit) _paths.Remove(_order.Dequeue());
		}
		return new FileGrant(token, Path.GetFileName(full));
	}

	/// <exception cref="UnknownFileGrantException">The token was never issued here, or has been forgotten.</exception>
	public string Resolve(string token)
	{
		lock (_gate) return _paths.TryGetValue(token, out var path) ? path : throw new UnknownFileGrantException();
	}
}

/// <summary>
/// Holds the paths of a drop between the host reading them off the message and the renderer's call that collects them
/// (<c>files:open-dropped</c>). Only a Zing view's message can offer, so a page can't make the host grant a file.
/// It is a single slot and is only safe because the host fills it, runs the one call that takes it and clears it on the
/// UI thread: WebView2 raises the message there, <c>Bridge.HandleAsync</c> runs the handler (which takes the offer) before its
/// first await, and the clear follows on the UI thread's context, so no other message's offer can sit between them.
/// </summary>
public sealed class DropInbox(FileGrants grants)
{
	private readonly object _gate = new();
	private IReadOnlyList<string>? _offered;

	/// <exception cref="UnauthorizedAccessException"><paramref name="source"/> isn't one of Zing's own pages.</exception>
	public void Offer(string source, IReadOnlyList<string> paths)
	{
		if (!ZingOrigin.IsZingView(source)) throw new UnauthorizedAccessException($"A drop from {source} was refused: only Zing's own views may hand the host a file");
		lock (_gate) _offered = paths;
	}

	/// <summary>Forgets an offer nobody collected.</summary>
	public void Clear()
	{
		lock (_gate) _offered = null;
	}

	/// <exception cref="NoDroppedFilesException">Nothing was offered, or it was already collected.</exception>
	public IReadOnlyList<FileGrant> Take()
	{
		IReadOnlyList<string> paths;
		lock (_gate)
		{
			paths = _offered ?? throw new NoDroppedFilesException();
			_offered = null;
		}
		return [.. paths.Select(grants.Issue)];
	}
}

/// <summary>The kinds of file the picker is asked for, as the extensions it filters by.</summary>
public static class FileKinds
{
	private static readonly Dictionary<string, string[]> Kinds = new(StringComparer.Ordinal)
	{
		["image"] = ["jpg", "jpeg", "png", "gif", "webp", "bmp", "tif", "tiff", "heic", "heif", "avif", "svg", "ico", "cr2", "nef", "arw", "dng"],
		["video"] = ["mp4", "mov", "mkv", "avi", "webm", "m4v", "wmv", "flv", "mpg", "mpeg", "3gp"],
		["audio"] = ["mp3", "wav", "flac", "aac", "m4a", "ogg", "opus", "wma", "aiff"],
		["document"] = ["pdf", "docx", "doc", "odt", "rtf", "txt", "md", "html", "htm", "epub", "tex", "rst"],
		["pdf"] = ["pdf"],
		["picture"] = ["png", "jpg", "jpeg", "webp"],
		["csv"] = ["csv"],
		["any"] = [],
	};

	/// <summary>The extensions to offer, or none for every file ("any" is among the kinds).</summary>
	public static IReadOnlyList<string> Extensions(IReadOnlyList<string> kinds)
	{
		List<string> extensions = [];
		foreach (var kind in kinds)
		{
			if (!Kinds.TryGetValue(kind, out var known)) throw new UnknownFileKindException(kind);
			if (known.Length == 0) return [];
			extensions.AddRange(known);
		}
		return extensions;
	}
}
