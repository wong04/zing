using System.Text.RegularExpressions;
using Zing.Core.Viewer;

namespace Zing.Core.Engines;

public class SaveRefusedException(string message) : Exception(message);

/// <summary>The file is longer than its format allows.</summary>
public sealed class SaveTooLargeException(string message) : SaveRefusedException(message);

/// <summary>This window has already saved as much as it may this way.</summary>
public sealed class SaveBudgetException(string message) : SaveRefusedException(message);

/// <summary>
/// A tool result (the background remover's PNG, a PDF tool's PDF or JPEG). The worker PUTs the file it made to the host (a streamed request body, never base64 or
/// a bridge message); the host checks the declared and the real size, the format's magic bytes and the name, and puts the file in
/// Downloads under a name that is not taken (never over a file), through a hidden part file so a failure never leaves half a PNG
/// under a real name. The copy is made with asynchronous IO, so the thread that called is not held.
/// </summary>
public static partial class ResultSave
{

	// Windows' forbidden characters and controls are out; so is a leading dot, which the part files use.
	[GeneratedRegex(@"\A[^.\\/:*?""<>|\x00-\x1f][^\\/:*?""<>|\x00-\x1f]{0,148}\.[A-Za-z]{3}\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
	private static partial Regex PlainName();

	/// <summary>Checks a name without writing, and finds the format its extension asks for.</summary>
	/// <exception cref="SaveRefusedException">Not a plain name ending in one of the allowed extensions.</exception>
	public static SaveFormat CheckName(string name, IReadOnlyCollection<SaveFormat> allowed)
	{
		var wanted = string.Join(" or ", allowed.Select(f => f.Extension));
		if (!PlainName().IsMatch(name)) throw new SaveRefusedException($"\"{name}\" is not a plain name ending in {wanted}");
		var format = allowed.FirstOrDefault(f => name.EndsWith(f.Extension, StringComparison.OrdinalIgnoreCase)) ?? throw new SaveRefusedException($"\"{name}\" is not a plain name ending in {wanted}");
		if (SaveFormats.All.Any(f => name.Contains(f.Extension + ".", StringComparison.OrdinalIgnoreCase))) throw new SaveRefusedException($"\"{name}\" is not a plain name ending in {wanted}");
		return format;
	}

	/// <summary>Writes the PNG in <paramref name="body"/> and returns where it went and how long it is.</summary>
	/// <param name="declared">The request's Content-Length, when it has one; checked before anything is read.</param>
	/// <param name="room">How many more bytes this window may save this way.</param>
	/// <exception cref="SaveTooLargeException">Declared or actually longer than the format cap.</exception>
	/// <exception cref="SaveBudgetException">It would take the window past <paramref name="room"/>.</exception>
	/// <exception cref="SaveRefusedException">The name is not a plain name with an allowed extension, or the bytes are not that format.</exception>
	public static async Task<(string Path, long Bytes)> WriteAsync(string directory, string name, IReadOnlyCollection<SaveFormat> allowed, Stream body, long? declared, long room, CancellationToken cancel)
	{
		var format = CheckName(name, allowed);
		if (declared is { } length)
		{
			if (length > format.Cap) throw TooLarge(format);
			if (length > room) throw OutOfRoom();
		}
		Directory.CreateDirectory(directory);
		var part = Path.Combine(directory, $".{Guid.NewGuid():N}.zing-part");
		try
		{
			long written;
			await using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
			{
				written = await Copy(body, output, format, Math.Min(format.Cap, room), cancel);
			}
			var target = MoveIntoPlace(part, directory, name);
			return (target, written);
		}
		finally
		{
			if (File.Exists(part)) File.Delete(part);
		}
	}

	private static async Task<long> Copy(Stream body, FileStream output, SaveFormat format, long limit, CancellationToken cancel)
	{
		var buffer = new byte[81920];
		long total = 0;
		while (true)
		{
			var read = await body.ReadAsync(buffer, cancel);
			if (read == 0) break;
			if (total == 0 && (read < format.Magic.Length || !buffer.AsSpan(0, format.Magic.Length).SequenceEqual(format.Magic))) throw NotThat(format);
			total += read;
			if (total > limit) throw limit < format.Cap ? OutOfRoom() : TooLarge(format);
			await output.WriteAsync(buffer.AsMemory(0, read), cancel);
		}
		if (total < format.Magic.Length) throw NotThat(format);
		return total;
	}

	private static SaveRefusedException NotThat(SaveFormat format) => new($"That is not a {format.Extension.TrimStart('.').ToUpperInvariant()} file");

	private static SaveTooLargeException TooLarge(SaveFormat format) => new($"The file is more than {format.Cap / (1024 * 1024)} MB, which Zing will not save");

	private static SaveBudgetException OutOfRoom() => new("Zing has saved as much result data as it will in one session. Close Zing and open it again to save more");

	private static string MoveIntoPlace(string part, string directory, string name)
	{
		// File.Move refuses to overwrite, so a file that arrives after the name was chosen makes the next number be tried.
		while (true)
		{
			var target = Path.Combine(directory, DownloadNames.FileName(name, candidate => File.Exists(Path.Combine(directory, candidate))));
			try
			{
				File.Move(part, target, overwrite: false);
				return target;
			}
			catch (IOException) when (File.Exists(target))
			{
			}
		}
	}
}
