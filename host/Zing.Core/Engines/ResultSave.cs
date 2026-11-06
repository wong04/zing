using System.Text.RegularExpressions;
using Zing.Core.Viewer;

namespace Zing.Core.Engines;

public class SaveRefusedException(string message) : Exception(message);

/// <summary>The body stopped arriving for too long.</summary>
public sealed class SaveStalledException(string message) : Exception(message);

/// <summary>The file is longer than its format allows.</summary>
public sealed class SaveTooLargeException(string message) : SaveRefusedException(message);

/// <summary>This window has already saved as much as it may this way.</summary>
public sealed class SaveBudgetException(string message) : SaveRefusedException(message);

/// <summary>
/// A tool result (the background remover's PNG, a PDF tool's PDF or JPEG). The worker PUTs the file it made to the host (a streamed request body, never base64 or
/// a bridge message); the host checks the declared and the real size, the format's magic bytes and the name, and puts the file in
/// Downloads as "Never overwrite files" says (see OutputPlacement), through a hidden part file so a failure never leaves half a PNG
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
	/// <param name="idle">How long the body may go without a byte before the save is given up (<see cref="SaveStalledException"/>).</param>
	/// <param name="stillOpen">Asked just before the file is moved into place; false means the session it belongs to was closed, and nothing lands.</param>
	/// <param name="room">How many more bytes this window may save this way.</param>
	/// <exception cref="SaveTooLargeException">Declared or actually longer than the format cap.</exception>
	/// <exception cref="SaveBudgetException">It would take the window past <paramref name="room"/>.</exception>
	/// <exception cref="SaveRefusedException">The name is not a plain name with an allowed extension, or the bytes are not that format.</exception>
	public static async Task<(string Path, long Bytes)> WriteAsync(OutputPlacement placement, string directory, string name, IReadOnlyCollection<SaveFormat> allowed, Stream body, long? declared, long room, TimeSpan idle, Func<bool> stillOpen, CancellationToken cancel)
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
				written = await Copy(body, output, format, Math.Min(format.Cap, room), declared, idle, cancel);
			}
			// The session may have been closed while the body arrived; then the file does not land.
			cancel.ThrowIfCancellationRequested();
			if (!stillOpen()) throw new OperationCanceledException("The session was closed");
			var target = placement.Place(part, directory, name);
			return (target, written);
		}
		finally
		{
			if (File.Exists(part)) File.Delete(part);
		}
	}

	private static async Task<long> Copy(Stream body, FileStream output, SaveFormat format, long limit, long? declared, TimeSpan idle, CancellationToken cancel)
	{
		var buffer = new byte[81920];
		long total = 0;
		var checkedMagic = false;
		var head = new byte[format.Magic.Length];
		var headFilled = 0;
		// A stream that never ends must not hold the request and the part file for ever: each read gets the idle time, no more.
		using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
		while (true)
		{
			stall.CancelAfter(idle);
			int read;
			try
			{
				read = await body.ReadAsync(buffer, stall.Token);
			}
			catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
			{
				throw new SaveStalledException("The upload stalled, so Zing gave it up");
			}
			if (read == 0) break;
			total += read;
			if (declared is { } said && total > said) throw new SaveRefusedException("The file was longer than its Content-Length said");
			if (total > limit) throw limit < format.Cap ? OutOfRoom() : TooLarge(format);
			await output.WriteAsync(buffer.AsMemory(0, read), cancel);
			// The first bytes may arrive in pieces; the magic is judged once there are enough of them.
			if (!checkedMagic)
			{
				var take = Math.Min(read, head.Length - headFilled);
				buffer.AsSpan(0, take).CopyTo(head.AsSpan(headFilled));
				headFilled += take;
				if (headFilled == head.Length)
				{
					if (!head.AsSpan().SequenceEqual(format.Magic)) throw NotThat(format);
					checkedMagic = true;
				}
			}
		}
		if (!checkedMagic) throw NotThat(format);
		if (declared is { } expected && total != expected) throw new SaveRefusedException("The upload stopped early, so the file is incomplete");
		return total;
	}

	private static SaveRefusedException NotThat(SaveFormat format) => new($"That is not a {format.Extension.TrimStart('.').ToUpperInvariant()} file");

	private static SaveTooLargeException TooLarge(SaveFormat format) => new($"The file is more than {format.Cap / (1024 * 1024)} MB, which Zing will not save");

	private static SaveBudgetException OutOfRoom() => new("Zing has saved as much result data as it will in one session. Close Zing and open it again to save more");

}
