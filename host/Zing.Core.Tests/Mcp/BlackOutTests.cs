using Zing.Core.Mcp;

namespace Zing.Core.Tests.Mcp;

public sealed class BlackOutTests
{
	private static byte[] White(int width, int height) => Enumerable.Repeat((byte)255, width * height * 4).ToArray();

	private static bool IsBlack(byte[] bgra, int width, int x, int y) => bgra[(y * width + x) * 4] == 0 && bgra[(y * width + x) * 4 + 2] == 0;

	[Fact]
	public void ABoxIsWidenedToWholePixelsAndNothingElseChanges()
	{
		var picture = White(4, 3);

		BlackOut.Paint(picture, 4, 3, [new PixelBox(0.5, 0.5, 1, 1)]);

		Assert.True(IsBlack(picture, 4, 0, 0));
		Assert.True(IsBlack(picture, 4, 1, 1));
		Assert.False(IsBlack(picture, 4, 2, 1));
		Assert.False(IsBlack(picture, 4, 0, 2));
	}

	[Fact]
	public void ABoxPastTheEdgeIsClipped()
	{
		var picture = White(2, 2);

		BlackOut.Paint(picture, 2, 2, [new PixelBox(-5, 1, 50, 50)]);

		Assert.True(IsBlack(picture, 2, 0, 1));
		Assert.True(IsBlack(picture, 2, 1, 1));
		Assert.False(IsBlack(picture, 2, 0, 0));
	}

	[Fact]
	public void PixelsOfTheWrongSizeAreAnError()
	{
		Assert.Throws<ArgumentException>(() => BlackOut.Paint(new byte[3], 1, 1, []));
	}
}
