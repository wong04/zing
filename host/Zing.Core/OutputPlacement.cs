namespace Zing.Core;

/// <summary>
/// Puts a finished file into a folder under the name a tool chose, the way "Never overwrite files" (preserveOriginals) says
/// at that moment: on, a clashing name gets a number ("cat 2.png") and the old file is kept; off, a file of that name is replaced.
/// Every tool that leaves a file in Downloads places it through here: engine outputs, view.zing saves, HTML to PDF and page save.
/// </summary>
public sealed class OutputPlacement(Func<bool> neverOverwrite)
{
	private const int MaxNumbering = 1000;

	/// <summary>A placement that reads the setting each time a file is placed.</summary>
	public static OutputPlacement ForSettings(SettingsStore settings) =>
		new(() => settings.Get("preserveOriginals")?.GetValue<bool>() ?? throw new InvalidDataException("Setting preserveOriginals is missing"));

	/// <summary>Moves <paramref name="source"/> into <paramref name="directory"/> and returns where it went.</summary>
	/// <exception cref="IOException">No free name was found, or the move failed.</exception>
	public string Place(string source, string directory, string name) => Place(source, directory, name, () => { });

	/// <param name="beforeMove">Runs just before each move is tried (a test seam for a file that arrives in between).</param>
	public string Place(string source, string directory, string name, Action beforeMove)
	{
		if (!neverOverwrite()) return Replace(source, directory, name, beforeMove);
		return PlaceNumbered(source, directory, name, beforeMove);
	}

	/// <summary>
	/// Places the file under a free name whatever the setting says. For a job with several outputs: if a later one fails, the
	/// ones already placed are undone, and a file one of them had replaced could not come back.
	/// </summary>
	public string PlaceNumbered(string source, string directory, string name, Action beforeMove)
	{
		// The name is chosen against what exists, and File.Move refuses to overwrite, so a file that arrives in between makes the next number be tried.
		for (var attempt = 0; attempt < MaxNumbering; attempt++)
		{
			var target = Path.Combine(directory, DownloadNames.FileName(name, candidate => Taken(Path.Combine(directory, candidate))));
			beforeMove();
			try
			{
				File.Move(source, target, overwrite: false);
				return target;
			}
			catch (IOException) when (Taken(target))
			{
				// Taken in the instant since it was checked; choose again.
			}
		}
		throw new IOException($"Couldn't find a free name for {name} in {directory}");
	}

	/// <summary>A file of that name is replaced. A folder of that name can't be, so the name is numbered past it.</summary>
	private static string Replace(string source, string directory, string name, Action beforeMove)
	{
		var target = Path.Combine(directory, DownloadNames.FileName(name, candidate => Directory.Exists(Path.Combine(directory, candidate))));
		beforeMove();
		File.Move(source, target, overwrite: true);
		return target;
	}

	private static bool Taken(string path) => File.Exists(path) || Directory.Exists(path);
}
