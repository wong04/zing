using Zing.Core;

namespace Zing.Core.Tests;

public sealed class SharpIconTests
{
	[Theory]
	[InlineData("https://mail.google.com/favicon.ico", true)]
	[InlineData("http://127.0.0.1:8080/a.png", true)]
	[InlineData("zing://start/icon.png", false)]
	[InlineData("data:image/png;base64,AAAA", false)]
	[InlineData("not a url", false)]
	public void OnlyWebIconsAreFetched(string uri, bool fetched) => Assert.Equal(fetched, SharpIcon.CanFetch(uri));

	[Fact]
	public void TheLargestFrameOfAnIcoIsChosen() =>
		Assert.Equal(2, SharpIcon.BestFrame([(16, 16), (32, 32), (48, 48), (24, 24)]));

	[Fact]
	public void AnIconNoBiggerThanTheEnginesIsNotWorthKeeping()
	{
		Assert.Null(SharpIcon.BestFrame([(16, 16)]));
		Assert.Null(SharpIcon.BestFrame([]));
	}

	[Theory]
	[InlineData(32, 32, 32, 32)]
	[InlineData(64, 64, 64, 64)]
	[InlineData(180, 180, 64, 64)]
	[InlineData(256, 128, 64, 32)]
	[InlineData(20, 40, 20, 40)]
	public void AnIconIsKeptAtItsOwnSizeUpTo64(uint width, uint height, uint keptWidth, uint keptHeight) =>
		Assert.Equal((keptWidth, keptHeight), SharpIcon.KeptSize(width, height));

	[Fact]
	public void AnSvgIsRecognisedSoItIsNotDecoded()
	{
		Assert.True(SharpIcon.IsSvg("  <?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray()));
		Assert.True(SharpIcon.IsSvg("<svg viewBox=\"0 0 16 16\"></svg>"u8.ToArray()));
		Assert.False(SharpIcon.IsSvg([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
		Assert.False(SharpIcon.IsSvg([0x00, 0x00, 0x01, 0x00]));
	}
}
