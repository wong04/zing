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

/// <summary>An argument, file name or address the renderer sent isn't one an engine may be given.</summary>
public sealed class UnsafeEngineArgumentException(string reason) : ArgumentException(reason);

public sealed class TooManyJobsException(int limit)
	: InvalidOperationException($"Zing runs at most {limit} engine jobs at a time. Wait for one to finish or cancel it, then try again");

public sealed class UnknownJobException(string jobId)
	: InvalidOperationException($"No engine job {jobId} is running for this window");

/// <summary>The engine ended cleanly but didn't make a file the job was promised.</summary>
public sealed class EngineOutputMissingException(string name)
	: IOException($"The engine finished without making {name}");

/// <summary>A file token the renderer sent was never issued by the file picker or a drop in this window.</summary>
public sealed class UnknownFileGrantException()
	: UnauthorizedAccessException("That file wasn't picked or dropped in this window, so Zing won't read it");

public sealed class NoDroppedFilesException()
	: InvalidOperationException("No files were dropped");

public sealed class UnknownFileKindException(string kind)
	: ArgumentException($"{kind} isn't a kind of file Zing can ask for (image, video, audio, document, any)");

/// <summary>The engine is being downloaded or updated, so it can't be started or leased until that ends.</summary>
public sealed class EngineBusyException(string engineId)
	: InvalidOperationException($"{EngineCatalog.NameOf(engineId)} is being downloaded or updated right now. Wait for that to finish, then try again")
{
	public string EngineId { get; } = engineId;
}
