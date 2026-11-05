using System.Diagnostics;
using System.IO.Compression;

namespace Zing.Core.Engines;

/// <summary>
/// Unpacks a downloaded engine into an empty staging folder. Every entry name is checked against the
/// folder before anything is written (zip slip), and what was unpacked is checked again afterwards.
/// </summary>
internal static class EngineArchive
{
	public static void MoveSingle(Engine engine, string downloaded, string stage)
	{
		Directory.CreateDirectory(stage);
		var destination = Path.Combine(stage, engine.Executable);
		Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
		File.Move(downloaded, destination);
	}

	public static void ExtractZip(Engine engine, string archive, string stage, long maxBytes, CancellationToken cancel)
	{
		try
		{
			using var zip = ZipFile.OpenRead(archive);
			long declared = 0;
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var entry in zip.Entries)
			{
				CheckName(engine, stage, entry.FullName);
				if (entry.Name != "" && !seen.Add(entry.FullName.Replace('\\', '/'))) throw new EngineDownloadException(engine.Id, engine.Url, $"the archive lists {entry.FullName} more than once");
				declared += entry.Length;
				if (declared > maxBytes) throw new EngineDownloadException(engine.Id, engine.Url, "the archive unpacks to more than Zing allows");
			}
			Directory.CreateDirectory(stage);
			long written = 0;
			foreach (var entry in zip.Entries)
			{
				cancel.ThrowIfCancellationRequested();
				var destination = Path.GetFullPath(Path.Combine(stage, entry.FullName));
				if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
				{
					Directory.CreateDirectory(destination);
					continue;
				}
				Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
				using var source = entry.Open();
				using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
				written += CopyLimited(engine, source, target, maxBytes - written);
			}
		}
		catch (InvalidDataException error)
		{
			throw new EngineDownloadException(engine.Id, engine.Url, "the download isn't a readable zip archive", error);
		}
	}

	// The sizes in a zip's index can lie, so the bytes actually inflated are counted too.
	private static long CopyLimited(Engine engine, Stream source, Stream target, long allowance)
	{
		var buffer = new byte[1 << 16];
		long total = 0;
		int read;
		while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
		{
			total += read;
			if (total > allowance) throw new EngineDownloadException(engine.Id, engine.Url, "the archive unpacks to more than Zing allows");
			target.Write(buffer, 0, read);
		}
		return total;
	}

	/// <summary>
	/// 7z via Windows' own tar.exe (libarchive), which .NET has no reader for. Entry names are read from a
	/// listing and checked first; links in what comes out are refused after.
	/// </summary>
	public static void Extract7z(Engine engine, string archive, string stage, CancellationToken cancel)
	{
		var tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
		if (!File.Exists(tar)) throw new EngineDownloadException(engine.Id, engine.Url, "Windows' tar.exe, which unpacks this download, is missing");
		var listing = RunTar(engine, tar, ["-tf", archive], cancel);
		foreach (var name in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries)) CheckName(engine, stage, name.TrimEnd('\r'));
		Directory.CreateDirectory(stage);
		RunTar(engine, tar, ["-xf", archive, "-C", stage], cancel);
		RefuseLinks(engine, stage);
	}

	private static string RunTar(Engine engine, string tar, string[] args, CancellationToken cancel)
	{
		var start = new ProcessStartInfo(tar) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
		foreach (var arg in args) start.ArgumentList.Add(arg);
		using var process = Process.Start(start) ?? throw new EngineDownloadException(engine.Id, engine.Url, "tar.exe didn't start");
		using var stop = cancel.Register(() => Stop(process));
		var errors = process.StandardError.ReadToEndAsync();
		var output = process.StandardOutput.ReadToEnd();
		process.WaitForExit();
		cancel.ThrowIfCancellationRequested();
		if (process.ExitCode != 0) throw new EngineDownloadException(engine.Id, engine.Url, ExplainTarFailure(errors.Result));
		return output;
	}

	// A cancel can land just as tar finishes; killing an exited process throws, and that is not a failure.
	private static void Stop(Process process)
	{
		try
		{
			process.Kill(entireProcessTree: true);
		}
		catch (InvalidOperationException)
		{
			// Already exited.
		}
	}

	/// <summary>Says what tar's failure most likely means, always keeping tar's own words.</summary>
	internal static string ExplainTarFailure(string stderr)
	{
		var text = stderr.Trim();
		if (text.Contains("Unrecognized archive format", StringComparison.OrdinalIgnoreCase) || text.Contains("Unsupported", StringComparison.OrdinalIgnoreCase))
			return $"this Windows' tar.exe can't read 7z archives (it needs a Windows 11 build); tar said: {text}";
		if (text.Contains("too long", StringComparison.OrdinalIgnoreCase) || text.Contains("ENAMETOOLONG", StringComparison.Ordinal))
			return $"a file path is too long for Windows to create; tar said: {text}";
		return $"tar.exe couldn't unpack it ({text})";
	}

	private static void RefuseLinks(Engine engine, string stage)
	{
		var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, ReturnSpecialDirectories = false };
		foreach (var entry in new DirectoryInfo(stage).EnumerateFileSystemInfos("*", options))
		{
			if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new UnsafeArchiveException(engine.Id, engine.Url, $"a link: {Path.GetRelativePath(stage, entry.FullName)}");
		}
	}

	private static readonly char[] Separators = ['/', '\\'];

	/// <summary>Refuses a name that is absolute, has a drive or stream colon, climbs with "..", or resolves outside <paramref name="stage"/>.</summary>
	internal static void CheckName(Engine engine, string stage, string name)
	{
		var unsafeName = name.Length == 0
			|| name.Contains(':')
			|| name.Contains('\0')
			|| name[0] is '/' or '\\'
			|| name.Split(Separators).Contains("..");
		if (unsafeName) throw new UnsafeArchiveException(engine.Id, engine.Url, name);
		var root = Path.GetFullPath(stage) + Path.DirectorySeparatorChar;
		var resolved = Path.GetFullPath(Path.Combine(stage, name));
		if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) && resolved + Path.DirectorySeparatorChar != root) throw new UnsafeArchiveException(engine.Id, engine.Url, name);
	}
}
