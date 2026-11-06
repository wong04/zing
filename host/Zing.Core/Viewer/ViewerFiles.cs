using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Zing.Core.Engines;

namespace Zing.Core.Viewer;

/// <param name="Origin">The request's Origin header, if it has one.</param>
/// <param name="Referer">The request's Referer header, if it has one.</param>
/// <param name="Body">The request's body, for a PUT.</param>
/// <param name="ContentLength">The request's Content-Length, when it has one.</param>
public sealed record ViewerRequest(string Method, string Url, string? Range, string? Origin, string? Referer, Stream? Body = null, long? ContentLength = null);


/// <param name="Body">Empty for a reply without one, and when <paramref name="WholeFile"/> names the file to hand over.</param>
/// <param name="WholeFile">A finished file too big to buffer: the host streams it from disk instead.</param>
/// <param name="Lease">Held for as long as the host reads <paramref name="WholeFile"/> (the model must not be removed meanwhile); the host disposes it when it is done with the stream, or at once if it never opens one.</param>
public sealed record ViewerReply(int Status, string Reason, IReadOnlyDictionary<string, string> Headers, byte[] Body, string? WholeFile = null, IDisposable? Lease = null);

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

	/// <summary>The kinds of file this tool session was opened for (see <see cref="SessionKinds"/>); empty for a viewer file. Only a session with kinds takes a save, and the model is served while one has the image kind.</summary>
	public IReadOnlyList<SessionKind> Kinds { get; internal init; } = [];

	/// <summary>Cancelled when the session is closed, so a save still arriving stops and its file never lands.</summary>
	internal CancellationTokenSource Revoked { get; } = new();

	internal bool Reads(string kind) => Kinds.Any(k => k.Name == kind);

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
	private static readonly Regex ModelAddress = new("^/model/([a-z]{1,16})$", RegexOptions.CultureInvariant);
	private static readonly Regex SaveAddress = new("^/save/([A-Za-z0-9_-]{32})/([^/]{1,400})$", RegexOptions.CultureInvariant);
	private static readonly Regex ByteRange = new(@"^bytes=(\d*)-(\d*)$", RegexOptions.CultureInvariant);
	private static readonly Regex OwnerFolder = new(@"^(\d+)_", RegexOptions.CultureInvariant);

	private readonly string _root;
	private readonly Action<string, Exception> _onError;
	private readonly object _gate = new();
	private readonly Dictionary<string, ViewerSession> _sessions = new(StringComparer.Ordinal);
	// Folders of files closed while their engine still ran: deleted again when its exit arrives, since it may place the file after the close.
	private readonly Dictionary<string, string> _awaitingExit = new(StringComparer.Ordinal);
	private long _saved;
	private long _reserved;

	/// <summary>The installed background model's file with a hold on it (the engine store will not remove it while held); throws what <c>EngineStore.InstalledPath</c> throws. Null on a host with no engines.</summary>
	public Func<(string Path, IDisposable Lease)>? ModelOpener { get; set; }

	/// <summary>Gives a saved file a token the renderer can hand back to other tools; null leaves it out of the answer.</summary>
	public Func<string, string>? IssueToken { get; set; }

	/// <summary>Where tool results go (the Downloads folder).</summary>
	public string? SaveDirectory { get; set; }

	/// <summary>How many more bytes this window may save for the background remover; about a gigabyte, so a runaway page can't fill the disk.</summary>
	internal long SaveBudget { get; set; } = 1L << 30;

	/// <summary>How long an upload may go without a byte before it is given up.</summary>
	internal TimeSpan SaveIdleTimeout { get; set; } = TimeSpan.FromSeconds(30);

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

	/// <summary>
	/// Opens a read session for a tool panel (the background remover, the PDF tools) on a file the person picked. The file must be one of
	/// <paramref name="kinds"/> by its own first bytes (never its name) and within that kind length cap, here and now. The address
	/// served is the only thing the tool is told: the path and the token stay in the host, and closing the panel, opening another
	/// file in it or <see cref="Close"/> revokes it. The same session takes the tool result back (<c>PUT /save/&lt;id&gt;/&lt;name&gt;</c>).
	/// </summary>
	/// <exception cref="SessionFileRefusedException">Not one of the kinds, or longer than a kind allows.</exception>
	public ViewerSession OpenSession(string path, string panel, IReadOnlyList<SessionKind> kinds)
	{
		string contentType;
		using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
		{
			var head = new byte[16];
			var read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
			var matches = kinds.Select(kind => (Kind: kind, Type: kind.Sniff(head.AsSpan(0, read)))).FirstOrDefault(m => m.Type is not null);
			if (matches.Type is null) throw new SessionFileRefusedException($"This tool takes {string.Join(" and ", kinds.Select(k => k.Advice))}. Use Convert to make one first, or open this file in the viewer");
			if (file.Length > matches.Kind.ReadCap) throw new SessionFileRefusedException($"This file is {file.Length / (1024 * 1024)} MB, more than this tool reads ({matches.Kind.ReadCap / (1024 * 1024)} MB)");
			contentType = matches.Type;
		}
		var format = new ViewerFormat(ViewerKind.Native, contentType.StartsWith("image/", StringComparison.Ordinal) ? "image" : "pdf", contentType);
		return Add(panel, id => new ViewerSession(id, panel, Path.GetFileName(path), format, path, null) { Kinds = kinds });
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
			session.Revoked.Cancel();
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
		var address = Parse(request.Url);
		if (address is null) return NotFound();
		var method = request.Method.ToUpperInvariant();
		// A tool is writing or negotiating CORS: only the Origin header counts. A Referer is accepted for reading (an image or video element sends no Origin).
		if (!(method is "PUT" or "OPTIONS" ? FromZingOrigin(request) : FromZingView(request))) return NotFound();
		try
		{
			return address.Kind switch
			{
				AddressKind.Model => ServeModel(method),
				AddressKind.Save => method == "OPTIONS" ? Preflight() : await SaveAsync(address, method, request),
				_ => await ServeSessionAsync(address.Token, method, request),
			};
		}
		catch (Exception gone) when (gone is FileNotFoundException or DirectoryNotFoundException or EngineNotInstalledException)
		{
			// A file deleted or moved since it was opened, or a model not downloaded; a growing copy is told apart below.
			return NotFoundForView();
		}
		catch (EngineBusyException)
		{
			return Failure(503, "Service Unavailable", "The model is being changed right now. Try again in a moment");
		}
		catch (Exception io) when (io is IOException or UnauthorizedAccessException && address.Kind != AddressKind.Session)
		{
			return Unreadable(io);
		}
		catch (Exception invalid) when (invalid is InvalidOperationException or ArgumentException && address.Kind != AddressKind.Session)
		{
			_onError("serve a file", invalid);
			return Failure(500, "Internal Server Error", "Zing could not serve that");
		}
	}

	/// <summary>
	/// A file locked by another program, access denied, a name too long (PathTooLongException is an IOException): the page is told,
	/// the host error hook records it, and nothing reaches the UI thread as an exception.
	/// </summary>
	private ViewerReply Unreadable(Exception io)
	{
		_onError("serve a file", io);
		return Failure(503, "Service Unavailable", "The file can't be read right now. It may be open in another program");
	}

	private async Task<ViewerReply> ServeSessionAsync(string token, string method, ViewerRequest request)
	{
		ViewerSession? session;
		lock (_gate) session = _sessions.GetValueOrDefault(token);
		// Past the origin check, so the page may read the answer.
		if (session is null) return NotFoundForView();
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
		catch (Exception io) when (io is IOException or UnauthorizedAccessException && !source.Growing)
		{
			// A finished file the person still has open elsewhere. A growing copy's refusal stays an error (see ServeGrowingAsync).
			return Unreadable(io);
		}
	}

	/// <summary>The background model, by its engine id, to a Zing view, and only while a picture is open: the file is handed to the host to stream from disk.</summary>
	private ViewerReply ServeModel(string method)
	{
		if (method == "OPTIONS") return Preflight();
		if (method is not ("GET" or "HEAD")) return Refuse(405, "Method Not Allowed");
		lock (_gate)
		{
			if (!_sessions.Values.Any(session => session.Reads("image"))) return NotFoundForView();
		}
		var (path, lease) = (ModelOpener ?? throw new InvalidOperationException("This host has no model to serve"))();
		try
		{
			var length = new FileInfo(path).Length;
			var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["Content-Type"] = "application/octet-stream",
				["Content-Length"] = length.ToString(CultureInfo.InvariantCulture),
				["Cache-Control"] = "no-store",
				["X-Content-Type-Options"] = "nosniff",
				["Referrer-Policy"] = "no-referrer",
				["Access-Control-Expose-Headers"] = "Content-Length",
			};
			if (method == "HEAD")
			{
				lease.Dispose();
				return new ViewerReply(200, "OK", Cors(headers), []);
			}
			// Opened and closed once here so a locked or just-removed file is a status, not a failure when the host opens it.
			OpenForReading(path).Dispose();
			return new ViewerReply(200, "OK", Cors(headers), [], path, lease);
		}
		catch
		{
			lease.Dispose();
			throw;
		}
	}

	/// <summary>A tool result, PUT by the tool of the session it names: its extension (from the session kinds) decides the magic check and the cap; refusals say why in plain text.</summary>
	private async Task<ViewerReply> SaveAsync(Address address, string method, ViewerRequest request)
	{
		ViewerSession? session;
		lock (_gate) session = _sessions.GetValueOrDefault(address.Token);
		if (session is null || session.Kinds.Count == 0) return NotFoundForView();
		if (method != "PUT") return Refuse(405, "Method Not Allowed");
		var directory = SaveDirectory ?? throw new InvalidOperationException("This host has nowhere to save to");
		if (request.Body is null) return Failure(400, "Bad Request", "The file came with no body");
		var allowed = session.Kinds.SelectMany(SaveFormats.For).DistinctBy(f => f.Extension).ToList();
		long reserve;
		try
		{
			// The most this save can take is held from the quota now, so saves running together cannot overshoot it.
			var format = ResultSave.CheckName(address.Name, allowed);
			reserve = request.ContentLength ?? format.Cap;
			lock (_gate)
			{
				if (reserve > format.Cap) reserve = -1;
				else if (reserve > SaveBudget - _saved - _reserved) reserve = -2;
				else _reserved += reserve;
			}
		}
		catch (SaveRefusedException refused)
		{
			return Failure(400, "Bad Request", refused.Message);
		}
		if (reserve == -1) return Failure(413, "Payload Too Large", $"The file is more than {allowed.Max(f => f.Cap) / (1024 * 1024)} MB, which Zing will not save");
		if (reserve == -2) return Failure(507, "Insufficient Storage", "Zing has saved as much result data as it will in one session. Close Zing and open it again to save more");
		long bytes = 0;
		try
		{
			var saved = await ResultSave.WriteAsync(directory, address.Name, allowed, request.Body, request.ContentLength, reserve, SaveIdleTimeout, () => !session.Revoked.IsCancellationRequested, session.Revoked.Token).ConfigureAwait(false);
			bytes = saved.Bytes;
			var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { name = Path.GetFileName(saved.Path), token = IssueToken?.Invoke(saved.Path) });
			var headers = Cors(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "application/json", ["Content-Length"] = json.Length.ToString(CultureInfo.InvariantCulture), ["Cache-Control"] = "no-store" });
			return new ViewerReply(201, "Created", headers, json);
		}
		catch (SaveTooLargeException tooLarge)
		{
			return Failure(413, "Payload Too Large", tooLarge.Message);
		}
		catch (SaveBudgetException budget)
		{
			return Failure(507, "Insufficient Storage", budget.Message);
		}
		catch (SaveStalledException stalled)
		{
			return Failure(408, "Request Timeout", stalled.Message);
		}
		catch (SaveRefusedException refused)
		{
			return Failure(400, "Bad Request", refused.Message);
		}
		catch (OperationCanceledException)
		{
			// The session was closed (Cancel, a new picture, the panel) while the file arrived: it does not land.
			return NotFoundForView();
		}
		catch (Exception io) when (io is IOException or UnauthorizedAccessException)
		{
			// Disk full, access denied, the folder gone: this is about writing, not reading.
			_onError("save a file", io);
			return Failure(500, "Internal Server Error", "The file can't be saved. The Downloads folder may be full or not writable");
		}
		finally
		{
			lock (_gate)
			{
				_reserved -= reserve;
				_saved += bytes;
			}
		}
	}

	/// <summary>A refusal the page may read, with the reason as plain text.</summary>
	private static ViewerReply Failure(int status, string reason, string message)
	{
		var text = Encoding.UTF8.GetBytes(message);
		var headers = Cors(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["Content-Type"] = "text/plain; charset=utf-8",
			["Content-Length"] = text.Length.ToString(CultureInfo.InvariantCulture),
			["Cache-Control"] = "no-store",
			["X-Content-Type-Options"] = "nosniff",
		});
		return new ViewerReply(status, reason, headers, text);
	}

	private enum AddressKind
	{
		Session,
		Model,
		Save,
	}

	private sealed record Address(AddressKind Kind, string Token, string Name);

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

	private static Address? Parse(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
		if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort) return null;
		if (url.Contains('?') || url.Contains('#')) return null;
		var path = uri.AbsolutePath;
		if (AddressPath.Match(path) is { Success: true } session) return new Address(AddressKind.Session, session.Groups[1].Value, "");
		// An engine id is plain letters: an escape is never meant, and .NET would turn %6D into m before the pattern saw it.
		if (!url.Contains('%') && ModelAddress.Match(path) is { Success: true } model)
		{
			return model.Groups[1].Value == EngineIds.BackgroundModel ? new Address(AddressKind.Model, "", model.Groups[1].Value) : null;
		}
		if (SaveAddress.Match(path) is { Success: true } save) return new Address(AddressKind.Save, save.Groups[1].Value, Uri.UnescapeDataString(save.Groups[2].Value));
		return null;
	}

	/// <summary>Only Zing's own pages may ask: by Origin when it is sent (a fetch), by Referer otherwise (an image or video element).</summary>
	private static bool FromZingOrigin(ViewerRequest request) => request.Origin == App;

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
			["Access-Control-Allow-Methods"] = "GET, HEAD, PUT, OPTIONS",
			["Access-Control-Allow-Headers"] = "Range, Content-Type",
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
		catch (Exception notThere) when (notThere is FileNotFoundException or DirectoryNotFoundException || (notThere is UnauthorizedAccessException && !File.Exists(path)))
		{
			// Not made yet, moved into place an instant before the exit arrives, or being deleted with its job folder: Windows refuses to
			// open a delete-pending file with access denied, and says the file is not there. A file that is there and refused is a real
			// error and is not caught.
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
