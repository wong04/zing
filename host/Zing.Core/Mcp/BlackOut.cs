namespace Zing.Core.Mcp;

/// <summary>A box on a screenshot, in its pixels.</summary>
public readonly record struct PixelBox(double X, double Y, double Width, double Height);

/// <summary>Paints vault-filled fields black on a screenshot's BGRA pixels before the AI sees it.</summary>
public static class BlackOut
{
	/// <summary>Each box is widened to whole pixels, so no edge of a field's text shows; boxes off the picture are clipped.</summary>
	public static void Paint(byte[] bgra, int width, int height, IEnumerable<PixelBox> boxes)
	{
		if (bgra.Length != width * height * 4) throw new ArgumentException($"{bgra.Length} bytes is not a {width}x{height} BGRA picture", nameof(bgra));
		foreach (var box in boxes)
		{
			var left = Math.Clamp((int)Math.Floor(box.X), 0, width);
			var top = Math.Clamp((int)Math.Floor(box.Y), 0, height);
			var right = Math.Clamp((int)Math.Ceiling(box.X + box.Width), 0, width);
			var bottom = Math.Clamp((int)Math.Ceiling(box.Y + box.Height), 0, height);
			for (var y = top; y < bottom; y++)
				for (var x = left; x < right; x++)
				{
					var at = (y * width + x) * 4;
					bgra[at] = 0;
					bgra[at + 1] = 0;
					bgra[at + 2] = 0;
					bgra[at + 3] = 255;
				}
		}
	}
}
