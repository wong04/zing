using System.Globalization;
using System.Text;

namespace Zing.Core;

/// <summary>The PDF tools were given a file or a name they won't handle; the reason is for the person.</summary>
public sealed class PdfFileRefusedException(string reason) : IOException(reason);

/// <summary>
/// How the PDF tools get a granted file's bytes and put a result in Downloads (channels <c>files:read</c> and
/// <c>files:save-bytes</c>). Both are narrow: reading takes only PDF, PNG, JPEG, GIF, WebP and BMP files (the pictures Convert can make into PDF pages) whose content is what the name says;
/// saving takes a plain name ending in pdf, png or jpg, checks the bytes are that kind, never overwrites, and answers a
/// numbered name. The bytes cross the bridge as base64 text, so both have a size limit (the same numbers are in
/// src/shared/pdf/pdfCore.ts, which a test compares).
/// </summary>
public static class PdfFiles
{
	public const long MaxReadBytes = 50L * 1024 * 1024;
	public const long MaxSaveBytes = 100L * 1024 * 1024;
	private const int MaxBaseLength = 100;

	private static readonly string[] ReadExtensions = ["pdf", "png", "jpg", "jpeg", "gif", "webp", "bmp"];
	private static readonly string[] SaveExtensions = ["pdf", "png", "jpg"];
	private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

	/// <summary>The size of base64 text that decodes to at most <see cref="MaxSaveBytes"/>; a longer text is refused unread.</summary>
	public static readonly long MaxSaveText = (MaxSaveBytes + 2) / 3 * 4;

	/// <exception cref="PdfFileRefusedException">Not a PDF or picture of a kind listed, by name and by content, or over <see cref="MaxReadBytes"/>.</exception>
	public static byte[] Read(string path)
	{
		var name = Path.GetFileName(path);
		var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
		if (!ReadExtensions.Contains(extension)) throw new PdfFileRefusedException($"{name} isn't a PDF or a picture (PNG, JPEG, GIF, WebP or BMP), so the PDF tools won't read it.");
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		if (stream.Length > MaxReadBytes) throw new PdfFileRefusedException($"{name} is {stream.Length / (1024 * 1024)} MB; the PDF tools read files up to {MaxReadBytes / (1024 * 1024)} MB.");
		var bytes = new byte[stream.Length];
		stream.ReadExactly(bytes);
		if (!ContentMatches(extension, bytes)) throw new PdfFileRefusedException($"{name} isn't really a {extension.ToUpperInvariant()} file.");
		return bytes;
	}

	/// <summary>Writes <paramref name="bytes"/> to <paramref name="downloads"/> as <paramref name="name"/>, numbered if taken, and returns the path.</summary>
	/// <exception cref="PdfFileRefusedException">The name isn't a plain name ending in pdf, png or jpg, the bytes aren't that kind, or they are over <see cref="MaxSaveBytes"/>.</exception>
	public static string Save(string downloads, string name, byte[] bytes)
	{
		var extension = CheckSaveName(name);
		if (bytes.LongLength > MaxSaveBytes) throw new PdfFileRefusedException($"That file is {bytes.LongLength / (1024 * 1024)} MB; the PDF tools save files up to {MaxSaveBytes / (1024 * 1024)} MB.");
		if (!ContentMatches(extension, bytes)) throw new PdfFileRefusedException($"The data isn't a {extension.ToUpperInvariant()} file, so it wasn't saved as {name}.");
		Directory.CreateDirectory(downloads);
		var part = Path.Combine(downloads, $".{Guid.NewGuid():N}.zing-part");
		try
		{
			File.WriteAllBytes(part, bytes);
			return PageSave.MoveIntoPlace(part, downloads, name);
		}
		finally
		{
			if (File.Exists(part)) File.Delete(part);
		}
	}

	private static string CheckSaveName(string name)
	{
		var dot = name.LastIndexOf('.');
		if (dot <= 0 || dot == name.Length - 1) throw new PdfFileRefusedException($"\"{name}\" isn't a name ending in .pdf, .png or .jpg.");
		var baseName = name[..dot];
		var extension = name[(dot + 1)..].ToLowerInvariant();
		if (!SaveExtensions.Contains(extension)) throw new PdfFileRefusedException($"\"{name}\" doesn't end in .pdf, .png or .jpg.");
		if (baseName.Length > MaxBaseLength) throw new PdfFileRefusedException($"The name is longer than {MaxBaseLength} characters.");
		// SafeFileBase would change a name with a path separator, a reserved name or an end of dots; "%" and brackets are patterns to engines that may later be given the file.
		if (DownloadNames.SafeFileBase(baseName) != baseName || baseName.IndexOfAny(['%', '[', ']']) >= 0 || baseName.Any(Invisible))
			throw new PdfFileRefusedException($"\"{name}\" isn't a plain file name.");
		return extension;
	}

	private static bool Invisible(char ch) =>
		char.GetUnicodeCategory(ch) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse;

	private static bool ContentMatches(string extension, byte[] bytes) => extension switch
	{
		// Readers accept the header anywhere in the first kilobyte.
		"pdf" => Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 1024)).Contains("%PDF-", StringComparison.Ordinal),
		"png" => bytes.AsSpan().StartsWith(PngSignature),
		"jpg" or "jpeg" => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
		"gif" => bytes.AsSpan().StartsWith("GIF87a"u8) || bytes.AsSpan().StartsWith("GIF89a"u8),
		"webp" => bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
		"bmp" => bytes.AsSpan().StartsWith("BM"u8),
		_ => false,
	};
}
