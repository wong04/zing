namespace Zing.Core.Viewer;

/// <summary>The file is not one of the kinds this panel may open, by its own first bytes, or is longer than the kind allows.</summary>
public sealed class SessionFileRefusedException(string message) : Exception(message);

public sealed class UnknownSessionKindException(string kind) : ArgumentException($"{kind} is not a kind of file Zing opens for a tool");

/// <summary>
/// A kind of file a tool panel may be given a read session for: judged by the file own first bytes, never its name, and by length.
/// The table is fixed in the host; a renderer names kinds, it cannot define one.
/// </summary>
/// <param name="Sniff">The content type the first bytes show, or null when they are not this kind.</param>
/// <param name="Advice">What to tell the person when a file is not this kind.</param>
public sealed record SessionKind(string Name, long ReadCap, Func<ReadOnlySpan<byte>, string?> Sniff, string Advice);

/// <summary>A result the host will save for a panel: the extension decides the magic check and the cap.</summary>
public sealed record SaveFormat(string Extension, byte[] Magic, long Cap);

public static class SessionKinds
{
	/// <summary>PNG, JPEG and WebP.</summary>
	public static readonly SessionKind Image = new("image", 120L * 1024 * 1024, SniffImage, "PNG, JPEG and WebP pictures");

	/// <summary>A PDF: <c>%PDF-</c> at the very start.</summary>
	public static readonly SessionKind Pdf = new("pdf", 50L * 1024 * 1024, head => head.StartsWith("%PDF-"u8) ? "application/pdf" : null, "PDF files");

	private static readonly Dictionary<string, SessionKind> Table = new(StringComparer.Ordinal) { [Image.Name] = Image, [Pdf.Name] = Pdf };

	/// <summary>The kinds by name; the renderer sends names only.</summary>
	/// <exception cref="UnknownSessionKindException">A name that is not in the table, or none at all.</exception>
	public static IReadOnlyList<SessionKind> Named(IEnumerable<string> names)
	{
		var kinds = names.Distinct(StringComparer.Ordinal).Select(name => Table.GetValueOrDefault(name) ?? throw new UnknownSessionKindException(name)).ToList();
		return kinds.Count == 0 ? throw new UnknownSessionKindException("(none)") : kinds;
	}

	private static string? SniffImage(ReadOnlySpan<byte> head)
	{
		if (head.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })) return "image/png";
		if (head.Length >= 3 && head[0] == 0xff && head[1] == 0xd8 && head[2] == 0xff) return "image/jpeg";
		if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
		return null;
	}
}

public static class SaveFormats
{
	public static readonly SaveFormat Png = new(".png", [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a], 64L * 1024 * 1024);
	public static readonly SaveFormat Jpg = new(".jpg", [0xff, 0xd8, 0xff], 64L * 1024 * 1024);
	public static readonly SaveFormat Pdf = new(".pdf", "%PDF-"u8.ToArray(), 50L * 1024 * 1024);

	public static IReadOnlyList<SaveFormat> All { get; } = [Png, Jpg, Pdf];

	/// <summary>What a session of this kind may save: images become PNGs (the background remover), PDFs become PDFs or JPEGs.</summary>
	public static IReadOnlyList<SaveFormat> For(SessionKind kind) => kind.Name == "pdf" ? [Pdf, Jpg] : [Png];
}
