using System.Text.Json.Nodes;

namespace Zing.Core.Viewer;

public enum ViewerKind
{
	/// <summary>Chromium shows it as it is.</summary>
	Native,

	/// <summary>ImageMagick makes a PNG.</summary>
	Image,

	/// <summary>FFmpeg makes a fragmented H.264 MP4 that the page plays as it grows.</summary>
	Video,

	/// <summary>FFmpeg makes an AAC M4A.</summary>
	Audio,
}

/// <param name="Show">What the page puts it in: image, video, audio or pdf.</param>
/// <param name="ContentType">What is served: chosen from the extension, never sniffed from the file.</param>
public sealed record ViewerFormat(ViewerKind Kind, string Show, string ContentType)
{
	public bool Converted => Kind != ViewerKind.Native;

	/// <summary>A converted video is played while FFmpeg is still writing it.</summary>
	public bool Stream => Kind == ViewerKind.Video;

	/// <summary>The fixed name of a converted copy; the file's own name never reaches a command line or the disk.</summary>
	public string? OutputName => Kind switch
	{
		ViewerKind.Image => "view.png",
		ViewerKind.Video => "view.mp4",
		ViewerKind.Audio => "view.m4a",
		_ => null,
	};
}

/// <summary>
/// What the viewer does with each extension. The lists are src/shared/convert/viewable.ts, generated into
/// Defaults/viewable.json, so the page and the host agree. HTML, SVG, XML and playlists are not in them.
/// </summary>
public static class ViewerFormats
{
	private static readonly Lazy<IReadOnlyDictionary<string, ViewerFormat>> Table = new(Load);

	/// <summary>The format for an extension ("heic", ".MOV"), or null when the viewer can't show it.</summary>
	public static ViewerFormat? For(string extension)
	{
		var name = extension.TrimStart('.').ToLowerInvariant();
		return Table.Value.GetValueOrDefault(name);
	}

	private static Dictionary<string, ViewerFormat> Load()
	{
		var defaults = ZingJson.LoadDefaults("viewable.json").AsObject();
		var table = new Dictionary<string, ViewerFormat>(StringComparer.Ordinal);
		foreach (var (ext, entry) in defaults["native"]!.AsObject())
		{
			table[ext] = new ViewerFormat(ViewerKind.Native, entry!["show"]!.GetValue<string>(), entry["contentType"]!.GetValue<string>());
		}
		AddConverted(table, defaults, "image", ViewerKind.Image, "image", "image/png");
		AddConverted(table, defaults, "video", ViewerKind.Video, "video", "video/mp4");
		AddConverted(table, defaults, "audio", ViewerKind.Audio, "audio", "audio/mp4");
		return table;
	}

	private static void AddConverted(Dictionary<string, ViewerFormat> table, JsonObject defaults, string list, ViewerKind kind, string show, string contentType)
	{
		foreach (var ext in defaults[list]!.AsArray())
		{
			var name = ext!.GetValue<string>();
			if (!table.TryAdd(name, new ViewerFormat(kind, show, contentType))) throw new InvalidDataException($"viewable.json lists {name} twice");
		}
	}
}

/// <summary>The viewer can't show this kind of file.</summary>
public sealed class UnsupportedViewerFormatException(string extension)
	: InvalidOperationException(extension.Length == 0 ? "The viewer can't show a file with no extension" : $"The viewer can't show .{extension} files");
