namespace Zing.Core.Engines;

public sealed class UnknownEngineException(string id) : ArgumentException($"{id} isn't an engine Zing knows (ffmpeg, magick, pandoc, ytdlp, bgmodel)");

/// <summary>
/// An engine couldn't be fetched, checked or unpacked. The message names the engine and gives the
/// address to fetch it by hand, as Settings shows it; nothing of the attempt is left on disk.
/// </summary>
public class EngineDownloadException(string engineId, Uri url, string reason, Exception? inner = null)
	: Exception($"Couldn't get {EngineCatalog.NameOf(engineId)}: {reason}. You can download it by hand from {url}", inner)
{
	public string EngineId { get; } = engineId;
	public Uri Url { get; } = url;
	public string Reason { get; } = reason;
}

public sealed class EngineChecksumException(string engineId, Uri url, string expected, string actual)
	: EngineDownloadException(engineId, url, $"the file doesn't match the checksum Zing expects (expected {expected}, got {actual})");

public sealed class EngineExecutableMissingException(string engineId, Uri url, string executable)
	: EngineDownloadException(engineId, url, $"the download didn't contain {executable}");

public sealed class UnsafeArchiveException(string engineId, Uri url, string entry)
	: EngineDownloadException(engineId, url, $"the archive holds an entry that would land outside the engine's folder ({entry})");

public sealed class EngineInUseException(string engineId, Exception? inner = null)
	: Exception($"{EngineCatalog.NameOf(engineId)} is in use right now. Close whatever is using it and try again", inner)
{
	public string EngineId { get; } = engineId;
}

public sealed class EngineNotInstalledException(string engineId)
	: InvalidOperationException($"{EngineCatalog.NameOf(engineId)} hasn't been downloaded yet")
{
	public string EngineId { get; } = engineId;
}
