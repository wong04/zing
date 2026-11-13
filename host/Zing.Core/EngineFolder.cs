namespace Zing.Core;

/// <summary>
/// The WebView2 user data folder, which one engine (browser process) uses at a time. A Zing that is quitting gives up its
/// profile before its engine has gone, so the next launch waits here for the old engine to let go: a second engine on a
/// folder still in use fails, or joins the dying one.
/// </summary>
public static class EngineFolder
{
	/// <summary>How long a launch waits for a quitting Zing's engine; with 30 loaded tabs it lets go in about a second.</summary>
	public static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

	private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(25);

	/// <summary>The file the engine holds open, unshared, for as long as its browser process runs.</summary>
	public static string LockFile(string userDataFolder) => Path.Combine(userDataFolder, "EBWebView", "lockfile");

	/// <summary>Returns once no engine holds the folder; throws <see cref="EngineFolderInUseException"/> if one still does after <paramref name="patience"/>.</summary>
	public static async Task WaitUntilFreeAsync(string userDataFolder, TimeSpan patience, CancellationToken cancel = default)
	{
		var lockFile = LockFile(userDataFolder);
		var deadline = DateTime.UtcNow + patience;
		while (InUse(lockFile))
		{
			if (DateTime.UtcNow >= deadline) throw new EngineFolderInUseException(userDataFolder, patience);
			await Task.Delay(Poll, cancel);
		}
	}

	private static bool InUse(string lockFile)
	{
		try
		{
			using var _ = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
			return false;
		}
		catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
		{
			return false;
		}
		// ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION: the engine has it open.
		catch (IOException held) when ((held.HResult & 0xFFFF) is 32 or 33)
		{
			return true;
		}
	}
}

public sealed class EngineFolderInUseException(string folder, TimeSpan patience)
	: IOException($"Another Zing's web engine still holds {folder} after {patience.TotalSeconds:0} s; close it, or wait for it to quit, and start Zing again");
