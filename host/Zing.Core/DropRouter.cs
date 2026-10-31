using System.Text;
using System.Text.RegularExpressions;

namespace Zing.Core;

/// <summary>What one dragged item offers, already read off the drag.</summary>
/// <param name="FileLocation">A file location: an OS path or a <c>file:</c> URL.</param>
/// <param name="Image">Raw image bytes: what a browser supplies when you drag an img.</param>
/// <param name="Url">A URL offered as a URL (text/uri-list), not as plain text.</param>
/// <param name="OtherTypes">MIME types of any other representations on offer, in the order offered.</param>
public sealed record DropPayload(
	string? FileLocation = null,
	byte[]? Image = null,
	string? Url = null,
	string? SuggestedName = null,
	IReadOnlyList<string>? OtherTypes = null);

public abstract record DropPlan
{
	/// <summary>A real file on disk: use it where it is.</summary>
	public sealed record Open(string Path) : DropPlan;

	/// <summary>Bytes to land in the inbox under this name.</summary>
	public sealed record Write(byte[] Bytes, string Name) : DropPlan;

	/// <summary>A web URL to download into the inbox (name it with <see cref="DropRouter.NameForDownload"/> once the response is in).</summary>
	public sealed record Download(string Url) : DropPlan;

	/// <summary>Ask the source for a file in each of these types, in order, until one arrives.</summary>
	public sealed record Materialise(IReadOnlyList<string> Types) : DropPlan;
}

/// <summary>
/// Turns anything dropped on the app into a local file (src/shared/shell/dropRouter.ts). A drag
/// from Explorer carries a file, but a drag out of a web page usually does not: it may be an
/// http(s) URL, raw image bytes, or some other representation. This decides what to do; the host
/// does the reading, writing and downloading.
/// </summary>
public static partial class DropRouter
{
	/// <summary>Where dropped web content lands, under the user's Downloads folder, so the user can find it again.</summary>
	public const string InboxFolderName = "Zing";

	/// <summary>Drag types a file drop target accepts. Never a wildcard: registering the root type made every view a universal drop target in Krackn.</summary>
	public static readonly IReadOnlyList<string> AcceptedDragTypes = ["Files", "text/uri-list", "text/plain"];

	/// <summary>For views that only want to notice a drag passing over them and never consume it.</summary>
	public static readonly IReadOnlyList<string> HoverDragTypes = ["Files", "text/uri-list"];

	private const string FallbackImageExtension = "png";
	private const string FallbackDownloadExtension = "bin";
	private const string FallbackDownloadName = "download";

	private static readonly Dictionary<string, string> ExtensionForMimeType = new()
	{
		["image/png"] = "png",
		["image/jpeg"] = "jpeg",
		["image/gif"] = "gif",
		["image/webp"] = "webp",
		["image/avif"] = "avif",
		["image/heic"] = "heic",
		["image/bmp"] = "bmp",
		["image/tiff"] = "tiff",
		["image/svg+xml"] = "svg",
		["image/x-icon"] = "ico",
		["image/vnd.microsoft.icon"] = "ico",
		["application/pdf"] = "pdf",
		["application/zip"] = "zip",
		["application/json"] = "json",
		["text/plain"] = "txt",
		["text/html"] = "html",
		["text/csv"] = "csv",
		["video/mp4"] = "mp4",
		["video/webm"] = "webm",
		["video/quicktime"] = "mov",
		["audio/mpeg"] = "mp3",
		["audio/wav"] = "wav",
		["audio/flac"] = "flac",
	};

	public static bool AcceptsDrag(IEnumerable<string> types) => types.Any(AcceptedDragTypes.Contains);

	public static bool NoticesDrag(IEnumerable<string> types) => types.Any(HoverDragTypes.Contains);

	/// <summary>What to do with one dropped item, cheapest first. Null when there is nothing file-like in it.</summary>
	public static DropPlan? PlanDrop(DropPayload payload, DateTimeOffset now)
	{
		// 1. A real file on disk: the Explorer case, and the cheapest.
		var path = payload.FileLocation is null ? null : AsFilePath(payload.FileLocation);
		if (path is not null) return new DropPlan.Open(path);

		// 2. Raw image bytes.
		if (payload.Image is { Length: > 0 } image)
			return new DropPlan.Write(image, NameForDroppedImage(image, payload.SuggestedName, now));

		// 3. A web URL: download it. The common case for dragging an image out of a page that only offers a link.
		var url = payload.Url is null ? null : AsWebUrl(payload.Url);
		if (url is not null) return new DropPlan.Download(url);

		// 4. Anything else with a file behind it. Text and URL types are excluded deliberately: a
		//    dragged string can be materialised into a temp file, so dropping a text selection onto a
		//    tool would silently manufacture a junk file in Downloads.
		var types = (payload.OtherTypes ?? []).Where(t => !t.StartsWith("text/", StringComparison.OrdinalIgnoreCase)).ToList();
		return types.Count > 0 ? new DropPlan.Materialise(types) : null;
	}

	/// <summary>An OS path for a dropped file location: a Windows path as given, or a <c>file:</c> URL converted. Null for anything else.</summary>
	public static string? AsFilePath(string location)
	{
		if (DrivePath().IsMatch(location) || location.StartsWith(@"\\", StringComparison.Ordinal)) return location;
		if (!location.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null;
		if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeFile) return null;
		var pathname = DecodePercent(uri.AbsolutePath);
		if (uri.Authority != "") return @"\\" + uri.Authority + pathname.Replace('/', '\\');
		return LeadingSlashBeforeDrive().Replace(pathname, "").Replace('/', '\\');
	}

	/// <summary>The URL if it is http(s), else null. Surrounding whitespace is tolerated.</summary>
	public static string? AsWebUrl(string item)
	{
		var text = item.Trim();
		if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
		if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
		return uri.AbsoluteUri;
	}

	/// <summary>Name for dropped image bytes. Without an extension, the real type is sniffed from the bytes rather than trusting the name.</summary>
	public static string NameForDroppedImage(byte[] bytes, string? suggestedName, DateTimeOffset now)
	{
		var name = SafeFileName(suggestedName ?? $"dropped-{now.ToUnixTimeSeconds()}");
		if (PathExtension(name) != "") return name;
		return $"{name}.{DetectExtension(bytes) ?? FallbackImageExtension}";
	}

	/// <summary>Name for a downloaded drop: the URL's last path component, given an extension from the response type or the bytes if it has none.</summary>
	public static string NameForDownload(string url, string? mimeType, byte[] bytes)
	{
		var name = SafeFileName(LastPathComponent(url));
		if (name == "") name = FallbackDownloadName;
		if (PathExtension(name) != "") return name;
		string? fromType = null;
		if (mimeType is not null) ExtensionForMimeType.TryGetValue(mimeType.ToLowerInvariant(), out fromType);
		return $"{name}.{fromType ?? DetectExtension(bytes) ?? FallbackDownloadExtension}";
	}

	/// <summary>Magic-number sniffing for the formats that actually turn up in drags.</summary>
	public static string? DetectExtension(byte[] bytes)
	{
		if (bytes.Length < 4) return null;
		if (StartsWith(bytes, 0, 0x89, 0x50, 0x4e, 0x47)) return "png";
		if (StartsWith(bytes, 0, 0xff, 0xd8, 0xff)) return "jpg";
		if (StartsWith(bytes, 0, 0x47, 0x49, 0x46)) return "gif";
		if (StartsWith(bytes, 0, 0x25, 0x50, 0x44, 0x46)) return "pdf";
		if (bytes.Length >= 12 && StartsWith(bytes, 0, 0x52, 0x49, 0x46, 0x46) && StartsWith(bytes, 8, 0x57, 0x45, 0x42, 0x50)) return "webp";
		if (bytes.Length >= 8 && StartsWith(bytes, 4, 0x66, 0x74, 0x79, 0x70)) return "mp4";
		return null;
	}

	/// <summary><paramref name="name"/>, or "name 2.ext", "name 3.ext"... the first that <paramref name="exists"/> says is free.</summary>
	public static string UniqueName(string name, Func<string, bool> exists)
	{
		var ext = PathExtension(name);
		var baseName = ext == "" ? name : name[..^(ext.Length + 1)];
		var candidate = name;
		for (var counter = 2; exists(candidate); counter++)
			candidate = ext == "" ? $"{baseName} {counter}" : $"{baseName} {counter}.{ext}";
		return candidate;
	}

	/// <summary>A name Windows will accept: the characters it reserves become "-", and the trailing dots and spaces it silently strips are removed up front.</summary>
	public static string SafeFileName(string name) => TrailingDotsAndSpaces().Replace(ReservedChars().Replace(name, "-"), "");

	/// <summary>Foundation's pathExtension: after the last dot, but a leading dot is not one.</summary>
	public static string PathExtension(string name)
	{
		var dot = name.LastIndexOf('.');
		return dot <= 0 || dot == name.Length - 1 ? "" : name[(dot + 1)..];
	}

	private static string LastPathComponent(string url)
	{
		var segments = new Uri(url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
		return segments.Length == 0 ? "" : DecodePercent(segments[^1]);
	}

	/// <summary>Decodes %XX escapes as UTF-8. Text that is not valid percent-encoding ("100%.png") is already literal and comes back unchanged, as decodeURIComponent's caller treats it.</summary>
	private static string DecodePercent(string text)
	{
		var bytes = new List<byte>();
		for (var i = 0; i < text.Length; i++)
		{
			if (text[i] != '%')
			{
				var length = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
				bytes.AddRange(Encoding.UTF8.GetBytes(text.Substring(i, length)));
				i += length - 1;
				continue;
			}
			if (i + 2 >= text.Length || !Uri.IsHexDigit(text[i + 1]) || !Uri.IsHexDigit(text[i + 2])) return text;
			bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
			i += 2;
		}
		try
		{
			return new UTF8Encoding(false, true).GetString(bytes.ToArray());
		}
		catch (DecoderFallbackException)
		{
			return text;
		}
	}

	private static bool StartsWith(byte[] bytes, int offset, params int[] prefix) =>
		bytes.Length >= offset + prefix.Length && prefix.Select((b, i) => bytes[offset + i] == b).All(match => match);

	[GeneratedRegex(@"^[A-Za-z]:[\\/]")]
	private static partial Regex DrivePath();

	[GeneratedRegex(@"^/(?=[A-Za-z]:)")]
	private static partial Regex LeadingSlashBeforeDrive();

	[GeneratedRegex(@"[<>:""/\\|?*\x00-\x1f]")]
	private static partial Regex ReservedChars();

	[GeneratedRegex(@"[. ]+\z")]
	private static partial Regex TrailingDotsAndSpaces();
}
