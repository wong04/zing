using System.Text.RegularExpressions;

namespace Zing.Core.Engines;

public sealed class PngSaveException(string message) : Exception(message);

/// <summary>
/// The background remover's result. The renderer sends the PNG it made as base64 and a plain name; the host checks the
/// size, the PNG signature and the name, and puts the file in Downloads under a name that is not taken (never over a
/// file), through a hidden part file so a failure never leaves half a PNG under a real name.
/// </summary>
public static partial class PngSave
{
	/// <summary>A 36-megapixel cut-out is about 40 MB at the most; this leaves room without letting the bridge carry unbounded text.</summary>
	public const int MaxBytes = 64 * 1024 * 1024;

	private static readonly byte[] Signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

	// Windows' forbidden characters and controls are out; so is a leading dot, which the part files use.
	[GeneratedRegex(@"\A[^.\\/:*?""<>|\x00-\x1f][^\\/:*?""<>|\x00-\x1f]{0,148}\.png\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
	private static partial Regex PlainName();

	/// <exception cref="PngSaveException">The text is longer than the cap allows, or is not base64.</exception>
	public static byte[] Decode(string base64)
	{
		if (base64.Length > MaxBytes / 3 * 4 + 4) throw new PngSaveException($"The picture is more than {MaxBytes / (1024 * 1024)} MB, which Zing will not save");
		try
		{
			return Convert.FromBase64String(base64);
		}
		catch (FormatException)
		{
			throw new PngSaveException("The picture arrived damaged (it is not valid base64)");
		}
	}

	/// <summary>Writes <paramref name="png"/> and returns where it went.</summary>
	/// <exception cref="PngSaveException">The name is not a plain .png name, or the bytes are over <see cref="MaxBytes"/> or are not a PNG.</exception>
	public static string Write(string directory, string name, byte[] png)
	{
		if (!PlainName().IsMatch(name) || name.Contains(".png.", StringComparison.OrdinalIgnoreCase))
			throw new PngSaveException($"\"{name}\" is not a plain name ending in .png");
		if (png.Length > MaxBytes) throw new PngSaveException($"The picture is more than {MaxBytes / (1024 * 1024)} MB, which Zing will not save");
		if (png.Length < Signature.Length || !png.AsSpan(0, Signature.Length).SequenceEqual(Signature)) throw new PngSaveException("That is not a PNG picture");

		Directory.CreateDirectory(directory);
		var part = Path.Combine(directory, $".{Guid.NewGuid():N}.zing-part");
		try
		{
			File.WriteAllBytes(part, png);
			return MoveIntoPlace(part, directory, name);
		}
		finally
		{
			if (File.Exists(part)) File.Delete(part);
		}
	}

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
