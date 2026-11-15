using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// A page's icon at its own size (Zing.Core.SharpIcon says why). The file is read with DevTools' Network.loadNetworkResource, so it
/// goes through the page's own network and cache, without cookies; an icon the engine just fetched usually costs no request.
/// </summary>
internal static class SharpFavicon
{
	/// <summary>WINCODEC_ERR_COMPONENTNOTFOUND: no decoder for the file's format.</summary>
	private const int UnknownFormat = unchecked((int)0x88982F50);

	/// <summary>
	/// The icon at <paramref name="iconUri"/> as a PNG data URL kept at up to SharpIcon.MaxSize, or null when there's nothing sharper than
	/// the engine's: not a web address, not loaded, too big, an SVG or another format Windows can't decode, or no bigger than 16 px.
	/// </summary>
	/// <param name="devTools">Calls a DevTools method on the page with JSON parameters and returns its JSON result.</param>
	public static async Task<string?> ReadAsync(Func<string, string, Task<string>> devTools, string iconUri)
	{
		if (!SharpIcon.CanFetch(iconUri)) return null;
		var file = await LoadAsync(devTools, iconUri);
		if (file is null || SharpIcon.IsSvg(file)) return null;
		var png = await ResizedPngAsync(file);
		return png is null ? null : "data:image/png;base64," + Convert.ToBase64String(png);
	}

	private static async Task<byte[]?> LoadAsync(Func<string, string, Task<string>> devTools, string url)
	{
		using var tree = JsonDocument.Parse(await devTools("Page.getFrameTree", "{}"));
		var frameId = tree.RootElement.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString();
		var request = JsonSerializer.Serialize(new { frameId, url, options = new { disableCache = false, includeCredentials = false } });
		using var loaded = JsonDocument.Parse(await devTools("Network.loadNetworkResource", request));
		var resource = loaded.RootElement.GetProperty("resource");
		if (!resource.GetProperty("success").GetBoolean() || !resource.TryGetProperty("stream", out var stream)) return null;
		var handle = stream.GetString()!;
		try
		{
			return await ReadStreamAsync(devTools, handle);
		}
		finally
		{
			await devTools("IO.close", JsonSerializer.Serialize(new { handle }));
		}
	}

	/// <summary>The whole stream, or null once it passes SharpIcon.MaxSourceBytes.</summary>
	private static async Task<byte[]?> ReadStreamAsync(Func<string, string, Task<string>> devTools, string handle)
	{
		using var memory = new MemoryStream();
		while (true)
		{
			using var chunk = JsonDocument.Parse(await devTools("IO.read", JsonSerializer.Serialize(new { handle, size = 64 * 1024 })));
			var root = chunk.RootElement;
			var data = root.GetProperty("data").GetString()!;
			var base64 = root.TryGetProperty("base64Encoded", out var encoded) && encoded.GetBoolean();
			var bytes = base64 ? Convert.FromBase64String(data) : System.Text.Encoding.UTF8.GetBytes(data);
			memory.Write(bytes);
			if (memory.Length > SharpIcon.MaxSourceBytes) return null;
			if (root.GetProperty("eof").GetBoolean()) return memory.ToArray();
		}
	}

	private static async Task<byte[]?> ResizedPngAsync(byte[] file)
	{
		using var source = new InMemoryRandomAccessStream();
		using (var writer = new DataWriter(source.GetOutputStreamAt(0)))
		{
			writer.WriteBytes(file);
			await writer.StoreAsync();
			// Disposing the writer would close the stream with it.
			writer.DetachStream();
		}
		source.Seek(0);
		BitmapDecoder decoder;
		try
		{
			decoder = await BitmapDecoder.CreateAsync(source);
		}
		catch (COMException error) when (error.HResult == UnknownFormat)
		{
			return null;
		}
		var sizes = new List<(uint Width, uint Height)>();
		for (uint i = 0; i < decoder.FrameCount; i++)
		{
			var each = await decoder.GetFrameAsync(i);
			sizes.Add((each.PixelWidth, each.PixelHeight));
		}
		if (SharpIcon.BestFrame(sizes) is not { } best) return null;
		var frame = await decoder.GetFrameAsync((uint)best);
		var (width, height) = SharpIcon.KeptSize(frame.PixelWidth, frame.PixelHeight);
		var transform = new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant };
		var pixels = await frame.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
		using var output = new InMemoryRandomAccessStream();
		var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
		encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, width, height, 96, 96, pixels.DetachPixelData());
		await encoder.FlushAsync();
		output.Seek(0);
		using var read = output.AsStreamForRead();
		using var png = new MemoryStream();
		await read.CopyToAsync(png);
		return png.ToArray();
	}
}
