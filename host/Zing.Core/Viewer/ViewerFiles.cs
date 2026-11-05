using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Zing.Core.Viewer;

/// <param name="Origin">The request's Origin header, if it has one.</param>
/// <param name="Referer">The request's Referer header, if it has one.</param>
public sealed record ViewerRequest(string Method, string Url, string? Range, string? Origin, string? Referer);

/// <param name="Body">Empty for a reply without one, and when <paramref name="WholeFile"/> names the file to hand over.</param>
/// <param name="WholeFile">A finished file too big to buffer: the host streams it from disk instead.</param>
public sealed record ViewerReply(int Status, string Reason, IReadOnlyDictionary<string, string> Headers, byte[] Body, string? WholeFile = null);

/// <summary>One file open in a viewer panel.</summary>
public sealed class ViewerSession
{
	internal ViewerSession(string id, string panel, string name, ViewerFormat format, string? source, string? folder)
	{
		Id = id;
		Panel = panel;
		Name = name;
		Format = format;
		Source = source;
		Folder = folder;
	}

	/// <summary>The random token in the address; it names the session and is the only thing that opens it.</summary>
	public string Id { get; }

	public string Panel { get; }

	/// <summary>The file's name, for showing.</summary>
	public string Name { get; }

	public ViewerFormat Format { get; }

	public string Url => $"https://{ViewerFiles.Host}/{Id}";

	public bool Stream => Format.Stream;

	/// <summary>The file served, when it is shown as it is; the person's own file, never copied.</summary>
	internal string? Source { get; }

	/// <summary>Where the converted copy is made and kept; null for a native file. Deleted when the file closes.</summary>
	public string? Folder { get; }

	public string? OutputName => Format.OutputName;

	/// <summary>The engine job making the copy, once started.</summary>
	public string? JobId { get; set; }

	internal ConversionState State { get; set; } = ConversionState.Running;

	/// <summary>The file FFmpeg is writing now, in its private job folder.</summary>
	internal string? Live { get; set; }

	/// <summary>The finished copy, in <see cref="Folder"/>.</summary>
	internal string? Final { get; set; }
}

internal enum ConversionState
{
	Running,
	Complete,
	Failed,
}

/// <summary>
/// The files open in one window's viewer panels, and what <c>https://view.zing/&lt;token&gt;</c> answers. A page tab never
/// gets here, and a request from one of Zing's own views is answered only for a token this window issued and still
/// has open (random, 192 bits), so anything else is a 404 that says nothing. Content types come from the extension and
/// go with <c>nosniff</c> and a CSP that sandboxes everything but a PDF. Ranges are read from disk a chunk at a time,
/// never the whole file, on a thread-pool thread: the host takes a deferral rather than blocking the UI thread. A
/// converted video is served while FFmpeg writes it: 204 means no new bytes yet, 416 means the true end.
/// </summary>
public sealed class ViewerFiles : IDisposable
{
	public const string Host = "view.zing";
	public const int MaxChunk = 4 * 1024 * 1024;

	/// <summary>A finished file asked for whole and no bigger than this is read into memory; a bigger one is handed over as a file.</summary>
	public const int WholeFileLimit = 8 * 1024 * 1024;

	private const string App = "https://" + ZingOrigin.Host;

	private static readonly Regex AddressPath = new("^/([A-Za-z0-9_-]{32})$", RegexOptions.CultureInvariant);
	private static readonly Regex ByteRange = new(@"^bytes=(\d*)-(\d*)$", RegexOptions.CultureInvariant);
	private static readonly Regex OwnerFolder = new(@"^(\d+)_", RegexOptions.CultureInvariant);

	private readonly string _root;
	private readonly Action<string, Exception> _onError;
	private readonly object _gate = new();
	private readonly Dictionary<string, ViewerSession> _sessions = new(StringComparer.Ordinal);
	// Folders of files closed while their engine still ran: deleted again when its exit arrives, since it may place the file after the close.
	private readonly Dictionary<string, string> _awaitingExit = new(StringComparer.Ordinal);

	/// <param name="root">Where converted copies go: a folder of Zing's own under the temp folder.</param>
	/// <param name="onError">Called for a copy that couldn't be deleted yet (the next start's sweep takes it).</param>
	public ViewerFiles(string root, Action<string, Exception> onError)
	{
		_root = root;
		_onError = onError;
	}

	/// <summary>Opens a file Chromium shows as it is. Nothing is copied: the address serves the file from where it is.</summary>
	public ViewerSession OpenNative(string path, string panel, ViewerFormat format)
	{
		if (format.Converted) throw new ArgumentException("Not a native format", nameof(format));
		return Add(panel, id => new ViewerSession(id, panel, Path.GetFileName(path), format, path, null));
	}

	/// <summary>Opens a file that needs a converted copy; its folder is made now and the caller starts the engine.</summary>
	public ViewerSession OpenConverted(string sourceName, string panel, ViewerFormat format)
	{
		if (!format.Converted) throw new ArgumentException("A native format needs no copy", nameof(format));
		return Add(panel, id =>
		{
			var folder = Path.Combine(_root, $"{Environment.ProcessId}_{id[..8]}");
			EnsureRealRoot(_root);
			Directory.CreateDirectory(folder);
			return new ViewerSession(id, panel, Path.GetFileName(sourceName), format, null, folder);
		});
	}

	private ViewerSession Add(string panel, Func<string, ViewerSession> create)
	{
		CloseWhere(session => session.Panel == panel);
		var id = Base64Url(RandomNumberGenerator.GetBytes(24));
		var session = create(id);
		lock (_gate) _sessions[id] = session;
		return session;
	}

	private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

	/// <summary>FFmpeg's output file is about to be written at <paramref name="path"/>; the address serves it while it grows.</summary>
	public void BeginLive(string id, string path)
	{
		lock (_gate)
		{
			if (_sessions.TryGetValue(id, out var session)) session.Live = path;
		}
	}

	/// <summary>The engine finished and its copy is at <paramref name="finalPath"/>. After a close the copy is deleted instead.</summary>
	public void Complete(string id, string finalPath)
	{
		var stale = Finish(id, session =>
		{
			session.Final = finalPath;
			session.State = ConversionState.Complete;
		});
		if (stale is not null) DeleteFolder(stale);
	}

	/// <summary>The conversion failed or was stopped: the address answers 404 from now on and whatever copy there is goes.</summary>
	public void Fail(string id)
	{
		string? folder = null;
		var stale = Finish(id, session =>
		{
			session.State = ConversionState.Failed;
			folder = session.Folder;
		});
		var gone = stale ?? folder;
		if (gone is not null) DeleteFolder(gone);
	}

	/// <summary>Returns the folder of a file that was closed before its engine ended, which is now due for deletion.</summary>
	private string? Finish(string id, Action<ViewerSession> update)
	{
		lock (_gate)
		{
			if (_sessions.TryGetValue(id, out var session))
			{
				update(session);
				return null;
			}
			return _awaitingExit.Remove(id, out var folder) ? folder : null;
		}
	}

	public ViewerSession? Find(string id)
	{
		lock (_gate) return _sessions.GetValueOrDefault(id);
	}

	public ViewerSession? ForPanel(string panel)
	{
		lock (_gate) return _sessions.Values.FirstOrDefault(session => session.Panel == panel);
	}

	/// <summary>Whether an address is on the viewer's host, whoever asks: a page tab's request for one is answered 404 without looking further.</summary>
	public static bool IsViewerAddress(string url) =>
		Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase);

	/// <summary>The reply for an address on the viewer's host that no Zing view of a window answers.</summary>
	public static ViewerReply Unavailable() => NotFound();

	/// <summary>Forgets the file: its address answers 404 and its copy is deleted. A close is not an error twice.</summary>
	public void Close(string id) => Remove(id, awaitExit: true);

	/// <summary>Like <see cref="Close"/>, for a file whose engine never started, so no exit will follow.</summary>
	public void Abandon(string id) => Remove(id, awaitExit: false);

	private void Remove(string id, bool awaitExit)
	{
		string? folder;
		lock (_gate)
		{
			if (!_sessions.Remove(id, out var session)) return;
			folder = session.Folder;
			if (folder is not null && awaitExit && session.State == ConversionState.Running) _awaitingExit[id] = folder;
		}
		if (folder is not null) DeleteFolder(folder);
	}

	/// <summary>The window's open panels (minimized ones included) are <paramref name="openPanels"/>; the files of any other panel close.</summary>
	public void CloseExcept(IReadOnlySet<string> openPanels) => CloseWhere(session => !openPanels.Contains(session.Panel));

	private void CloseWhere(Func<ViewerSession, bool> matches)
	{
		List<string> ids;
		lock (_gate) ids = [.. _sessions.Values.Where(matches).Select(session => session.Id)];
		foreach (var id in ids) Close(id);
	}

	public void Dispose() => CloseWhere(_ => true);

	/// <summary>How long a copy that a reader still holds waits before its one more try at deletion.</summary>
	internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);

	/// <summary>
	/// A reader that still has the file open (the engine's last read, Explorer's preview) makes the delete fail. It is tried once more after
	/// a short wait, on the thread pool so no caller waits; only a second failure is reported, and the next start's sweep takes the folder.
	/// </summary>
	private void DeleteFolder(string folder)
	{
		if (TryDelete(folder) is null) return;
		var delay = RetryDelay;
		_ = Task.Delay(delay).ContinueWith(_ =>
		{
			if (TryDelete(folder) is { } failure) _onError($"delete {folder}", failure);
		}, TaskScheduler.Default);
	}

	private static Exception? TryDelete(string folder)
	{
		try
		{
			if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
			return null;
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			return failure;
		}
	}

	// --- serving

	/// <summary>Answers one request for <c>https://view.zing/...</c> from one of this window's Zing views.</summary>
	public async Task<ViewerReply> ServeAsync(ViewerRequest request)
	{
		if (TokenOf(request.Url) is not { } token) return NotFound();
		if (!FromZingView(request)) return NotFound();
		ViewerSession? session;
		lock (_gate) session = _sessions.GetValueOrDefault(token);
		// Past the origin check, so the page may read the answer.
		if (session is null) return NotFoundForView();
		var method = request.Method.ToUpperInvariant();
		if (method == "OPTIONS") return Preflight();
		if (method is not ("GET" or "HEAD")) return Refuse(405, "Method Not Allowed");
		var source = Source(session);
		if (source is null) return NotFoundForView();
		try
		{
			return source.Growing ? await ServeGrowingAsync(session, source.Path, request, method) : await ServeFinishedAsync(session, source.Path!, request, method);
		}
		catch (Exception gone) when (gone is FileNotFoundException or DirectoryNotFoundException)
		{
			// A native file deleted or moved since it was opened; a growing one is told apart below.
			return NotFoundForView();
		}
	}

	/// <param name="Path">Null while a growing copy has no file yet.</param>
	private sealed record ServedFile(string? Path, bool Growing);

	private ServedFile? Source(ViewerSession session)
	{
		lock (_gate)
		{
			if (session.Source is not null) return new ServedFile(session.Source, false);
			return session.State switch
			{
				ConversionState.Complete => new ServedFile(session.Final!, false),
				ConversionState.Running when session.Stream => new ServedFile(session.Live, true),
				_ => null,
			};
		}
	}

	private static string? TokenOf(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
		if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort) return null;
		if (url.Contains('?') || url.Contains('#')) return null;
		var match = AddressPath.Match(uri.AbsolutePath);
		return match.Success ? match.Groups[1].Value : null;
	}

	/// <summary>Only Zing's own pages may ask: by Origin when it is sent (a fetch), by Referer otherwise (an image or video element).</summary>
	private static bool FromZingView(ViewerRequest request)
	{
		if (!string.IsNullOrEmpty(request.Origin)) return request.Origin == App;
		return !string.IsNullOrEmpty(request.Referer) && ZingOrigin.IsZingView(request.Referer);
	}

	private static ViewerReply NotFound() => Refuse(404, "Not Found");

	/// <summary>A 404 for a request that passed the origin check: the page may read it, so its fetch fails with a status instead of an opaque error.</summary>
	private static ViewerReply NotFoundForView()
	{
		var reply = NotFound();
		return reply with { Headers = Cors(reply.Headers) };
	}

	private static Dictionary<string, string> Cors(IReadOnlyDictionary<string, string> headers) =>
		new(headers, StringComparer.OrdinalIgnoreCase) { ["Access-Control-Allow-Origin"] = App, ["Vary"] = "Origin" };

	private static ViewerReply Refuse(int status, string reason) =>
		new(status, reason, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["Content-Length"] = "0",
			["Cache-Control"] = "no-store",
			["X-Content-Type-Options"] = "nosniff",
		}, []);

	private static Dictionary<string, string> Headers(ViewerSession session) => new(StringComparer.OrdinalIgnoreCase)
	{
		["Content-Type"] = session.Format.ContentType,
		["Accept-Ranges"] = "bytes",
		["Cache-Control"] = "no-store",
		["X-Content-Type-Options"] = "nosniff",
		// A PDF can't be shown by Chromium's viewer inside a sandbox; everything else is shown in an element and can't run anything.
		["Content-Security-Policy"] = session.Format.Show == "pdf" ? $"default-src 'none'; frame-ancestors {App}" : "sandbox; default-src 'none'; frame-ancestors 'none'",
		["Referrer-Policy"] = "no-referrer",
		["Access-Control-Allow-Origin"] = App,
		["Access-Control-Expose-Headers"] = "Content-Range, Content-Length, Accept-Ranges, X-Content-Type-Options, Content-Security-Policy",
		["Vary"] = "Origin",
	};

	private static ViewerReply Preflight() =>
		new(204, "No Content", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["Access-Control-Allow-Origin"] = App,
			["Access-Control-Allow-Methods"] = "GET, HEAD, OPTIONS",
			["Access-Control-Allow-Headers"] = "Range",
			["Access-Control-Max-Age"] = "600",
			["Cache-Control"] = "no-store",
			["Vary"] = "Origin",
		}, []);

	/// <summary>The bytes asked for as (start, end), either of which may be left open; null for a Range it can't make sense of.</summary>
	private static (long? Start, long? End)? ParseRange(string? header)
	{
		if (header is null) return (null, null);
		var match = ByteRange.Match(header.Trim());
		if (!match.Success) return null;
		var first = match.Groups[1].Value;
		var last = match.Groups[2].Value;
		if (first.Length == 0 && last.Length == 0) return null;
		if (!TryNumber(first, out var start) || !TryNumber(last, out var end)) return null;
		if (start is not null && end is not null && end < start) return null;
		return (start, end);
	}

	private static bool TryNumber(string text, out long? value)
	{
		value = null;
		if (text.Length == 0) return true;
		if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return false;
		value = parsed;
		return true;
	}

	private static FileStream OpenForReading(string path) =>
		new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous);

	private static async Task<byte[]> ReadAsync(FileStream file, long start, int count)
	{
		var buffer = new byte[count];
		var filled = 0;
		while (filled < count)
		{
			var read = await RandomAccess.ReadAsync(file.SafeFileHandle, buffer.AsMemory(filled), start + filled);
			if (read == 0) break;
			filled += read;
		}
		return filled == count ? buffer : buffer[..filled];
	}

	private async Task<ViewerReply> ServeFinishedAsync(ViewerSession session, string path, ViewerRequest request, string method)
	{
		if (ParseRange(request.Range) is not { } range) return Unsatisfiable(OpenLength(path));
		using var file = OpenForReading(path);
		var length = file.Length;
		var headers = Headers(session);
		if (request.Range is null)
		{
			headers["Content-Length"] = length.ToString(CultureInfo.InvariantCulture);
			if (method == "HEAD") return new ViewerReply(200, "OK", headers, []);
			if (length > WholeFileLimit) return new ViewerReply(200, "OK", headers, [], path);
			return new ViewerReply(200, "OK", headers, await ReadAsync(file, 0, (int)length));
		}
		long start;
		long end;
		if (range.Start is null)
		{
			// bytes=-N: the last N bytes.
			var last = Math.Min(range.End!.Value, length);
			if (last == 0) return Unsatisfiable(length);
			start = length - last;
			end = length - 1;
		}
		else
		{
			start = range.Start.Value;
			if (start >= length) return Unsatisfiable(length);
			end = Math.Min(Math.Min(range.End ?? length - 1, length - 1), start + MaxChunk - 1);
		}
		end = Math.Min(end, start + MaxChunk - 1);
		var count = (int)(end - start + 1);
		headers["Content-Range"] = $"bytes {start}-{end}/{length}";
		headers["Content-Length"] = count.ToString(CultureInfo.InvariantCulture);
		return new ViewerReply(206, "Partial Content", headers, method == "HEAD" ? [] : await ReadAsync(file, start, count));
	}

	private static long OpenLength(string path)
	{
		using var file = OpenForReading(path);
		return file.Length;
	}

	private static ViewerReply Unsatisfiable(long length)
	{
		var reply = Refuse(416, "Range Not Satisfiable");
		return reply with { Headers = new Dictionary<string, string>(reply.Headers, StringComparer.OrdinalIgnoreCase) { ["Content-Range"] = $"bytes */{length}" } };
	}

	/// <summary>
	/// The copy FFmpeg is still writing. Its length isn't known: bytes that exist are 206 with an unknown total, and a
	/// request past them is 204 (try again), which the page treats as "not converted yet". Reading it never waits for
	/// FFmpeg, so a request can't hold anything up.
	/// </summary>
	private async Task<ViewerReply> ServeGrowingAsync(ViewerSession session, string? path, ViewerRequest request, string method)
	{
		if (ParseRange(request.Range) is not { } range || (request.Range is not null && range.Start is null)) return Unsatisfiable(0);
		var start = range.Start ?? 0;
		if (path is null) return NotYet(session);
		FileStream file;
		try
		{
			file = OpenForReading(path);
		}
		catch (Exception notThere) when (notThere is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
		{
			// Not made yet, moved into place an instant before the exit arrives, or being deleted with its job folder (Windows refuses
			// to open a file that is delete-pending, with access denied).
			return NotYet(session);
		}
		using (file)
		{
			var length = file.Length;
			if (start >= length) return NotYet(session);
			var end = Math.Min(Math.Min(range.End ?? length - 1, length - 1), start + MaxChunk - 1);
			var count = (int)(end - start + 1);
			var headers = Headers(session);
			headers["Content-Range"] = $"bytes {start}-{end}/*";
			headers["Content-Length"] = count.ToString(CultureInfo.InvariantCulture);
			return new ViewerReply(206, "Partial Content", headers, method == "HEAD" ? [] : await ReadAsync(file, start, count));
		}
	}

	private ViewerReply NotYet(ViewerSession session)
	{
		// A conversion that failed while the page waited has nothing more to give.
		lock (_gate)
		{
			if (session.State == ConversionState.Failed) return NotFoundForView();
		}
		var headers = Headers(session);
		headers.Remove("Content-Type");
		headers.Remove("Content-Security-Policy");
		return new ViewerReply(204, "No Content", headers, []);
	}

	// --- the sweep at the next start

	/// <summary>
	/// Deletes what a Zing that was killed left in <paramref name="root"/>. A folder is named for the process that made it, and
	/// one whose Zing is still running is another profile's and stays. A junction or link inside is removed itself and
	/// never entered, so it can't take anything outside the folder with it.
	/// </summary>
	public static void SweepLeftovers(string root, Func<int, bool> isZingRunning, Action<string, Exception> onError)
	{
		if (!Directory.Exists(root)) return;
		if (IsLink(new DirectoryInfo(root)))
		{
			// Something else made the root a link (to a person's folder, say): only the link goes, never what it points at.
			EnsureRealRoot(root);
			return;
		}
		foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos().ToList())
		{
			var live = entry is DirectoryInfo && !IsLink(entry) && OwnerFolder.Match(entry.Name) is { Success: true } owner && int.TryParse(owner.Groups[1].Value, out var pid) && isZingRunning(pid);
			if (!live) RemoveWithoutFollowing(entry, onError);
		}
	}

	/// <summary>Makes sure <paramref name="root"/> is a real folder: a junction or link planted in its place is removed (the link only) and a folder made.</summary>
	private static void EnsureRealRoot(string root)
	{
		var info = new DirectoryInfo(root);
		if (info.Exists && IsLink(info)) info.Delete();
		Directory.CreateDirectory(root);
	}

	/// <summary>Whether a process of this id is a running Zing.</summary>
	public static bool IsZingRunning(int pid)
	{
		try
		{
			using var process = Process.GetProcessById(pid);
			return string.Equals(process.ProcessName, "Zing", StringComparison.OrdinalIgnoreCase);
		}
		catch (ArgumentException)
		{
			// No process has that id.
			return false;
		}
		catch (InvalidOperationException)
		{
			// It ended while being looked at.
			return false;
		}
	}

	private static bool IsLink(FileSystemInfo entry) => entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

	private static void RemoveWithoutFollowing(FileSystemInfo entry, Action<string, Exception> onError)
	{
		try
		{
			if (entry is DirectoryInfo directory && !IsLink(entry))
			{
				foreach (var child in directory.EnumerateFileSystemInfos().ToList()) RemoveWithoutFollowing(child, onError);
			}
			if (entry is FileInfo) entry.Attributes &= ~FileAttributes.ReadOnly;
			// Deleting a link removes the link and not what it points at.
			entry.Delete();
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			onError($"delete {entry.FullName}", failure);
		}
	}
}
