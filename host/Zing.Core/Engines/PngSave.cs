using System.Text.RegularExpressions;

namespace Zing.Core.Engines;

public class PngSaveException(string message) : Exception(message);

/// <summary>The picture is longer than <see cref="PngSave.MaxBytes"/>.</summary>
public sealed class PngTooLargeException(string message) : PngSaveException(message);

/// <summary>This window has already saved as much as it may this way.</summary>
public sealed class PngBudgetException(string message) : PngSaveException(message);

/// <summary>
/// The background remover's result. The worker PUTs the PNG it made to the host (a streamed request body, never base64 or
/// a bridge message); the host checks the declared and the real size, the PNG signature and the name, and puts the file in
/// Downloads under a name that is not taken (never over a file), through a hidden part file so a failure never leaves half a PNG
/// under a real name. The copy is made with asynchronous IO, so the thread that called is not held.
/// </summary>
public static partial class PngSave
{
	/// <summary>A 36-megapixel cut-out is about 40 MB at the most; this leaves room.</summary>
	public const int MaxBytes = 64 * 1024 * 1024;

	private static readonly byte[] Signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

	// Windows' forbidden characters and controls are out; so is a leading dot, which the part files use.
	[GeneratedRegex(@"\A[^.\\/:*?""<>|\x00-\x1f][^\\/:*?""<>|\x00-\x1f]{0,148}\.png\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
	private static partial Regex PlainName();

	/// <summary>Checks a name without writing: <see cref="PngSaveException"/> unless it is a plain name ending in .png.</summary>
	public static void CheckName(string name)
	{
		if (!PlainName().IsMatch(name) || name.Contains(".png.", StringComparison.OrdinalIgnoreCase))
			throw new PngSaveException($"\"{name}\" is not a plain name ending in .png");
	}

	/// <summary>Writes the PNG in <paramref name="body"/> and returns where it went and how long it is.</summary>
	/// <param name="declared">The request's Content-Length, when it has one; checked before anything is read.</param>
	/// <param name="room">How many more bytes this window may save this way.</param>
	/// <exception cref="PngTooLargeException">Declared or actually longer than <see cref="MaxBytes"/>.</exception>
	/// <exception cref="PngBudgetException">It would take the window past <paramref name="room"/>.</exception>
	/// <exception cref="PngSaveException">The name is not a plain .png name, or the bytes are not a PNG.</exception>
	public static async Task<(string Path, long Bytes)> WriteAsync(string directory, string name, Stream body, long? declared, long room, CancellationToken cancel)
	{
		CheckName(name);
		if (declared is { } length)
		{
			if (length > MaxBytes) throw TooLarge();
			if (length > room) throw OutOfRoom();
		}
		Directory.CreateDirectory(directory);
		var part = Path.Combine(directory, $".{Guid.NewGuid():N}.zing-part");
		try
		{
			long written;
			await using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
			{
				written = await Copy(body, output, Math.Min(MaxBytes, room), cancel);
			}
			var target = MoveIntoPlace(part, directory, name);
			return (target, written);
		}
		finally
		{
			if (File.Exists(part)) File.Delete(part);
		}
	}

	private static async Task<long> Copy(Stream body, FileStream output, long limit, CancellationToken cancel)
	{
		var buffer = new byte[81920];
		long total = 0;
		while (true)
		{
			var read = await body.ReadAsync(buffer, cancel);
			if (read == 0) break;
			if (total == 0 && (read < Signature.Length || !buffer.AsSpan(0, Signature.Length).SequenceEqual(Signature))) throw new PngSaveException("That is not a PNG picture");
			total += read;
			if (total > limit) throw limit < MaxBytes ? OutOfRoom() : TooLarge();
			await output.WriteAsync(buffer.AsMemory(0, read), cancel);
		}
		if (total < Signature.Length) throw new PngSaveException("That is not a PNG picture");
		return total;
	}

	private static PngTooLargeException TooLarge() => new($"The picture is more than {MaxBytes / (1024 * 1024)} MB, which Zing will not save");

	private static PngBudgetException OutOfRoom() => new("Zing has saved as much background-removed picture data as it will in one session. Close Zing and open it again to save more");

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
