using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zing.Core.Engines;

/// <param name="InstalledVersion">Null when the engine isn't downloaded.</param>
public sealed record EngineStatus(Engine Engine, string? InstalledVersion, long InstalledBytes);

public sealed record YtDlpUpdate(string Version, bool Changed);

/// <summary>
/// Keeps the engines in one folder, one subfolder each, and downloads one only when asked. Nothing is
/// fetched at startup or on a timer. A download lands in a temp folder beside the engines, is checked
/// against the pinned SHA-256 and unpacked there, and only then moved into place in one rename, so the
/// engine's folder is either absent or whole. Any failure or cancel deletes the temp folder; one that
/// couldn't be deleted goes at the next start.
/// </summary>
public sealed partial class EngineStore
{
	private const string TempPrefix = ".tmp-";
	private const string AsidePrefix = ".old-";
	private const string MarkerFile = ".zing-engine";
	private const long MaxExtractedBytes = 4L << 30;
	private const int UpdateSizeFactor = 2;
	private const long MaxChecksumFileBytes = 64 * 1024;
	private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

	private readonly string _root;
	private readonly EngineCatalog _catalog;
	private readonly HttpClient _http;
	// The release page's redirect must be read, not followed: only its first hop names the tag.
	private readonly HttpClient _noRedirect = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
	// The host runs all of this on the UI thread; tests and any other host may not, so the three tables are locked.
	private readonly object _gate = new();
	private readonly Action<string, Exception> _onError;
	private readonly Dictionary<string, Install> _installs = new(StringComparer.Ordinal);
	private readonly HashSet<string> _updating = new(StringComparer.Ordinal);
	private readonly Dictionary<string, int> _uses = new(StringComparer.Ordinal);

	/// <param name="onError">Called for a leftover folder that couldn't be deleted; it is tried again at the next start.</param>
	public EngineStore(string root, EngineCatalog catalog, HttpClient http, Action<string, Exception> onError)
	{
		_root = root;
		_catalog = catalog;
		_http = http;
		_onError = onError;
		RemoveLeftovers();
	}

	public EngineCatalog Catalog => _catalog;

	/// <summary>Test seam: runs once the download is complete and before it is checked again, so a test can swap the file.</summary>
	internal Action<string>? AfterDownload { get; set; }

	public IReadOnlyList<EngineStatus> List() => [.. _catalog.Engines.Select(Status)];

	/// <summary>
	/// The engine's executable, downloading it first if it isn't here. Callers that overlap share one
	/// download, and it stops only when every one of them has cancelled.
	/// </summary>
	public async Task<string> EnsureAsync(string id, IProgress<double> progress, CancellationToken cancel)
	{
		var engine = _catalog.Get(id);
		if (InstalledVersion(engine) is not null) return ExecutablePath(engine);
		Install install;
		lock (_gate)
		{
			if (!_installs.TryGetValue(id, out install!))
			{
				install = new Install();
				_installs[id] = install;
				install.Task = RunInstallAsync(engine, install);
			}
			install.Listeners.Add(progress);
			install.Waiters++;
		}
		try
		{
			return await install.Task.WaitAsync(cancel);
		}
		catch (OperationCanceledException) when (cancel.IsCancellationRequested)
		{
			bool last;
			// Out of the table before the stop begins, so a caller arriving now starts a fresh download instead of joining a doomed one.
			lock (_gate)
			{
				last = install.Waiters == 1 && !install.Task.IsCompleted;
				if (last) Forget(id, install);
			}
			if (last) await StopAsync(install);
			throw;
		}
		finally
		{
			lock (_gate) install.Waiters--;
		}
	}

	private void Forget(string id, Install install)
	{
		lock (_gate)
		{
			if (_installs.TryGetValue(id, out var current) && ReferenceEquals(current, install)) _installs.Remove(id);
		}
	}

	/// <summary>The last caller left: stop the download and wait until its temp folder is gone, so the caller sees a clean disk.</summary>
	private static async Task StopAsync(Install install)
	{
		await install.Cancel.CancelAsync();
		try
		{
			await install.Task;
		}
		catch (OperationCanceledException)
		{
			// That is the stop we asked for.
		}
	}

	/// <summary>The engine's executable if it is downloaded. Unlike <see cref="EnsureAsync"/> it never fetches: running a tool must not download anything the person didn't ask for.</summary>
	/// <exception cref="EngineNotInstalledException">It hasn't been downloaded.</exception>
	public string InstalledPath(string id)
	{
		var engine = _catalog.Get(id);
		return InstalledVersion(engine) is null ? throw new EngineNotInstalledException(id) : ExecutablePath(engine);
	}

	/// <summary>Marks the engine as running, so Remove refuses until the lease is disposed.</summary>
	public IDisposable Use(string id)
	{
		_catalog.Get(id);
		lock (_gate) _uses[id] = _uses.GetValueOrDefault(id) + 1;
		return new Lease(() =>
		{
			lock (_gate) _uses[id]--;
		});
	}

	/// <exception cref="EngineInUseException">While it downloads, while a lease is held, or while Windows says one of its files is open.</exception>
	public void Remove(string id)
	{
		_catalog.Get(id);
		lock (_gate)
		{
			if (_installs.ContainsKey(id) || _updating.Contains(id) || _uses.GetValueOrDefault(id) > 0) throw new EngineInUseException(id);
		}
		var target = FolderOf(id);
		if (!Directory.Exists(target)) return;
		// A rename is all or nothing and Windows refuses it while a file inside is open, so an engine is never half-deleted.
		var aside = MoveAside(id, target);
		Discard(aside);
	}

	/// <summary>
	/// Replaces yt-dlp with the newest release, but only when that release's own SHA2-256SUMS names the
	/// downloaded file's hash. The checksum file comes from the same host as the program, so this catches a corrupt or
	/// truncated download, not a compromised release. Called on a person's click, never on a schedule.
	/// </summary>
	public async Task<YtDlpUpdate> UpdateYtDlpAsync(CancellationToken cancel)
	{
		var current = _catalog.Get(EngineIds.YtDlp);
		var installed = InstalledVersion(current) ?? throw new EngineNotInstalledException(current.Id);
		lock (_gate)
		{
			if (_installs.ContainsKey(current.Id) || !_updating.Add(current.Id)) throw new EngineInUseException(current.Id);
		}
		try
		{
			var tag = await ReadLatestTagAsync(current, _catalog.YtDlpLatest, cancel);
			// Never a downgrade: "latest" can briefly name an older tag, and a newer one may already be here.
			if (EngineVersions.Compare(tag, installed) is <= 0) return new YtDlpUpdate(installed, false);
			var release = await ReleaseAsync(current, tag, cancel);
			var next = current with { Version = release.Version, Url = release.Program, Sha256 = release.Sha256 };
			await InstallAsync(next, exactBytes: null, maxBytes: current.Bytes * UpdateSizeFactor, _ => { }, cancel);
			return new YtDlpUpdate(release.Version, true);
		}
		finally
		{
			lock (_gate) _updating.Remove(current.Id);
		}
	}

	private sealed record Release(string Version, Uri Program, string Sha256);

	[GeneratedRegex(@"^\d{4}\.\d{2}\.\d{2}(\.\d+)?$")]
	private static partial Regex ReleaseTag();

	/// <summary>
	/// "latest" redirects once to the tagged release and then again to a storage host with no tag in its address, so only
	/// the first Location is read. It must be on the catalog's release host and path, and the tag a dated version; the
	/// program's address is then built from the catalog's base and the tag, never taken from a redirect.
	/// </summary>
	private async Task<Release> ReleaseAsync(Engine current, string tag, CancellationToken cancel)
	{
		var sums = new Uri(_catalog.YtDlpReleases, $"{tag}/SHA2-256SUMS");
		var program = new Uri(_catalog.YtDlpReleases, $"{tag}/{current.Executable}");
		HttpResponseMessage response;
		try
		{
			response = await _http.GetAsync(sums, HttpCompletionOption.ResponseHeadersRead, cancel);
		}
		catch (HttpRequestException error)
		{
			throw new EngineDownloadException(current.Id, sums, $"couldn't reach the release's checksum file ({error.Message})", error);
		}
		using (response)
		{
			if (!response.IsSuccessStatusCode) throw new EngineDownloadException(current.Id, sums, $"the checksum file's address answered {(int)response.StatusCode}");
			if (response.Content.Headers.ContentLength > MaxChecksumFileBytes) throw new EngineDownloadException(current.Id, sums, "the checksum file is larger than expected");
			string text;
			try
			{
				text = await response.Content.ReadAsStringAsync(cancel);
			}
			catch (Exception error) when (error is HttpRequestException or IOException)
			{
				throw new EngineDownloadException(current.Id, sums, $"the checksum file ended early ({error.Message})", error);
			}
			var hash = HashFor(text, current.Executable) ?? throw new EngineDownloadException(current.Id, sums, $"the release's checksum file has no SHA-256 for {current.Executable}");
			return new Release(tag, program, hash);
		}
	}

	private async Task<string> ReadLatestTagAsync(Engine current, Uri latest, CancellationToken cancel)
	{
		HttpResponseMessage response;
		try
		{
			response = await _noRedirect.GetAsync(latest, HttpCompletionOption.ResponseHeadersRead, cancel);
		}
		catch (HttpRequestException error)
		{
			throw new EngineDownloadException(current.Id, latest, $"couldn't reach the release page ({error.Message})", error);
		}
		using (response)
		{
			var redirected = (int)response.StatusCode is 301 or 302 or 303 or 307 or 308;
			if (!redirected || response.Headers.Location is null) throw new EngineDownloadException(current.Id, latest, $"the release page answered {(int)response.StatusCode} instead of pointing to a release, so there is no version to update to");
			var location = new Uri(latest, response.Headers.Location);
			var releases = _catalog.YtDlpReleases;
			var sameOrigin = location.Scheme == releases.Scheme && string.Equals(location.Host, releases.Host, StringComparison.OrdinalIgnoreCase) && location.Port == releases.Port;
			var path = location.AbsolutePath;
			if (!sameOrigin || !path.StartsWith(releases.AbsolutePath, StringComparison.Ordinal)) throw new EngineDownloadException(current.Id, location, "the release address isn't on the host Zing expects, so it names no version Zing trusts");
			var parts = path[releases.AbsolutePath.Length..].Split('/');
			if (parts.Length != 2 || parts[1] != "SHA2-256SUMS" || !ReleaseTag().IsMatch(parts[0])) throw new EngineDownloadException(current.Id, location, "the release address didn't name a dated version");
			return parts[0];
		}
	}

	[GeneratedRegex(@"^(?<hash>[0-9a-fA-F]{64})\s+\*?(?<name>\S+)\s*$")]
	private static partial Regex SumsLine();

	private static string? HashFor(string sums, string name)
	{
		foreach (var line in sums.Split('\n'))
		{
			var match = SumsLine().Match(line.TrimEnd('\r'));
			if (match.Success && match.Groups["name"].Value == name) return match.Groups["hash"].Value.ToLowerInvariant();
		}
		return null;
	}

	private sealed class Install
	{
		public Task<string> Task { get; set; } = System.Threading.Tasks.Task.FromResult("");
		public CancellationTokenSource Cancel { get; } = new();
		public List<IProgress<double>> Listeners { get; } = [];
		public int Waiters { get; set; }
	}

	private sealed class Lease(Action release) : IDisposable
	{
		private bool _released;

		public void Dispose()
		{
			if (_released) return;
			_released = true;
			release();
		}
	}

	private async Task<string> RunInstallAsync(Engine engine, Install install)
	{
		try
		{
			await InstallAsync(engine, engine.Bytes, engine.Bytes, fraction => { foreach (var listener in install.Listeners.ToArray()) listener.Report(fraction); }, install.Cancel.Token);
			return ExecutablePath(engine);
		}
		finally
		{
			Forget(engine.Id, install);
		}
	}

	private string FolderOf(string id) => Path.Combine(_root, id);

	private string ExecutablePath(Engine engine) => Path.Combine(FolderOf(engine.Id), engine.Executable);

	private sealed record Marker(string Version, string Sha256);

	/// <summary>
	/// The installed version, or null when there is none Zing accepts. The marker holds the version and the hash it was
	/// verified against, so a pin changed in a newer Zing (a security fix, say) reinstalls without re-hashing anything here.
	/// A yt-dlp updated by a click stays while it is newer than the pin; once the pin overtakes it, the pin wins.
	/// </summary>
	private string? InstalledVersion(Engine engine)
	{
		var markerFile = Path.Combine(FolderOf(engine.Id), MarkerFile);
		if (!File.Exists(ExecutablePath(engine)) || !File.Exists(markerFile)) return null;
		Marker? marker;
		try
		{
			marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(markerFile), ZingJson.Options);
		}
		catch (JsonException)
		{
			return null;
		}
		if (marker is null) return null;
		if (string.Equals(marker.Sha256, engine.Sha256, StringComparison.OrdinalIgnoreCase)) return marker.Version;
		if (engine.Id == EngineIds.YtDlp && EngineVersions.Compare(marker.Version, engine.Version) is > 0) return marker.Version;
		return null;
	}

	private EngineStatus Status(Engine engine)
	{
		var version = InstalledVersion(engine);
		if (version is null) return new EngineStatus(engine, null, 0);
		var size = new DirectoryInfo(FolderOf(engine.Id)).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
		return new EngineStatus(engine, version, size);
	}

	private async Task InstallAsync(Engine engine, long? exactBytes, long maxBytes, Action<double> report, CancellationToken cancel)
	{
		Directory.CreateDirectory(_root);
		var temp = Path.Combine(_root, $"{TempPrefix}{engine.Id}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(temp);
		try
		{
			var downloaded = Path.Combine(temp, "download");
			await DownloadAsync(engine, exactBytes, maxBytes, downloaded, report, cancel);
			AfterDownload?.Invoke(downloaded);
			// The stream was hashed as it arrived; the file is hashed again now, since it has sat in a folder other programs can write to.
			var onDisk = await Hashing.FileAsync(downloaded, "SHA-256");
			if (!string.Equals(onDisk, engine.Sha256, StringComparison.OrdinalIgnoreCase)) throw new EngineChecksumException(engine.Id, engine.Url, engine.Sha256, onDisk);
			var stage = Path.Combine(temp, "stage");
			await Task.Run(() => Unpack(engine, downloaded, stage, cancel), cancel);
			if (!File.Exists(Path.Combine(stage, engine.Executable))) throw new EngineExecutableMissingException(engine.Id, engine.Url, engine.Executable);
			File.WriteAllText(Path.Combine(stage, MarkerFile), JsonSerializer.Serialize(new Marker(engine.Version, engine.Sha256), ZingJson.Options));
			Place(engine.Id, stage);
		}
		finally
		{
			Discard(temp);
		}
	}

	private async Task DownloadAsync(Engine engine, long? exactBytes, long maxBytes, string path, Action<double> report, CancellationToken cancel)
	{
		HttpResponseMessage response;
		try
		{
			response = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Get, engine.Url), HttpCompletionOption.ResponseHeadersRead, cancel);
		}
		catch (HttpRequestException error)
		{
			throw new EngineDownloadException(engine.Id, engine.Url, $"couldn't connect ({error.Message})", error);
		}
		using (response)
		{
			if (!response.IsSuccessStatusCode) throw new EngineDownloadException(engine.Id, engine.Url, $"the server answered {(int)response.StatusCode} {response.ReasonPhrase}");
			var offered = response.Content.Headers.ContentLength;
			if (offered > maxBytes) throw Larger(engine, maxBytes);
			var (total, hash) = await CopyAndHashAsync(engine, response, maxBytes, exactBytes ?? offered, path, report, cancel);
			if (exactBytes is { } want && total != want) throw new EngineDownloadException(engine.Id, engine.Url, $"the download ended after {total} of {want} bytes");
			if (!string.Equals(hash, engine.Sha256, StringComparison.OrdinalIgnoreCase)) throw new EngineChecksumException(engine.Id, engine.Url, engine.Sha256, hash);
		}
	}

	private static EngineDownloadException Larger(Engine engine, long maxBytes) =>
		new(engine.Id, engine.Url, $"the server's file is larger than the {maxBytes} bytes expected");

	private static async Task<(long Total, string Hash)> CopyAndHashAsync(Engine engine, HttpResponseMessage response, long maxBytes, long? expected, string path, Action<double> report, CancellationToken cancel)
	{
		using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		long total = 0;
		await using var body = await response.Content.ReadAsStreamAsync(cancel);
		await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
		var buffer = new byte[1 << 16];
		while (true)
		{
			int read;
			using (var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel))
			{
				stall.CancelAfter(StallTimeout);
				try
				{
					read = await body.ReadAsync(buffer, stall.Token);
				}
				catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
				{
					throw new EngineDownloadException(engine.Id, engine.Url, $"no data arrived for {StallTimeout.TotalSeconds:0} seconds");
				}
				catch (Exception error) when (error is IOException or HttpRequestException)
				{
					throw new EngineDownloadException(engine.Id, engine.Url, $"the connection ended after {total} bytes ({error.Message})", error);
				}
			}
			if (read == 0) break;
			total += read;
			if (total > maxBytes) throw Larger(engine, maxBytes);
			sha.AppendData(buffer, 0, read);
			await file.WriteAsync(buffer.AsMemory(0, read), cancel);
			if (expected is > 0) report(Math.Min(1.0, (double)total / expected.Value));
		}
		report(1.0);
		return (total, Convert.ToHexStringLower(sha.GetHashAndReset()));
	}

	private static void Unpack(Engine engine, string downloaded, string stage, CancellationToken cancel)
	{
		var file = Path.GetFileName(engine.Url.AbsolutePath);
		if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) EngineArchive.ExtractZip(engine, downloaded, stage, MaxExtractedBytes, cancel);
		else if (file.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)) EngineArchive.Extract7z(engine, downloaded, stage, cancel);
		else EngineArchive.MoveSingle(engine, downloaded, stage);
	}

	/// <summary>The one rename that makes an install visible. A replaced engine is set aside first and kept until the new one is in.</summary>
	private void Place(string id, string stage)
	{
		var target = FolderOf(id);
		var aside = Directory.Exists(target) ? MoveAside(id, target) : null;
		try
		{
			Directory.Move(stage, target);
		}
		catch
		{
			if (aside is not null) Directory.Move(aside, target);
			throw;
		}
		if (aside is not null) Discard(aside);
	}

	private string MoveAside(string id, string target)
	{
		var aside = Path.Combine(_root, $"{AsidePrefix}{id}-{Guid.NewGuid():N}");
		try
		{
			Directory.Move(target, aside);
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			throw new EngineInUseException(id, error);
		}
		return aside;
	}

	private void Discard(string folder)
	{
		if (!Directory.Exists(folder)) return;
		try
		{
			Directory.Delete(folder, recursive: true);
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			_onError($"remove {folder}", error);
		}
	}

	// Zing runs one instance per profile (SingleInstance), and the engines folder follows the profile, so no other
	// process can be staging an install here while this one starts.
	private void RemoveLeftovers()
	{
		if (!Directory.Exists(_root)) return;
		foreach (var folder in Directory.GetDirectories(_root))
		{
			var name = Path.GetFileName(folder);
			if (name.StartsWith(TempPrefix, StringComparison.Ordinal) || name.StartsWith(AsidePrefix, StringComparison.Ordinal)) Discard(folder);
		}
	}
}
