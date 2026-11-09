using Zing.Core.LinkPreview;

namespace Zing.Core.Tests.LinkPreview;

public class PeekLayoutTests
{
	[Fact]
	public void InsetsThePageAreaBySixPercentAndTwentyFour()
	{
		var placement = PeekLayout.Place(new Rect(0, 80, 1200, 720), 0);
		Assert.Equal(new Rect(72, 104, 1056, 672), placement.Page);
	}

	[Fact]
	public void SideInsetIsAtLeastFortyEight()
	{
		var placement = PeekLayout.Place(new Rect(0, 80, 600, 720), 0);
		Assert.Equal(48, placement.Page.X);
		Assert.Equal(504, placement.Page.Width);
	}

	[Fact]
	public void WidthIsCappedAndCentred()
	{
		var placement = PeekLayout.Place(new Rect(0, 80, 3000, 720), 0);
		Assert.Equal(1400, placement.Page.Width);
		Assert.Equal(800, placement.Page.X);
	}

	[Fact]
	public void EachDepthInsetsSixteenMoreOnEverySide()
	{
		var placement = PeekLayout.Place(new Rect(0, 80, 1200, 720), 1);
		Assert.Equal(new Rect(88, 120, 1024, 640), placement.Page);
	}

	[Fact]
	public void ButtonsSitInTheRightMarginTopAlignedWithThePage()
	{
		var placement = PeekLayout.Place(new Rect(0, 80, 1200, 720), 0);
		Assert.Equal(new Rect(1136, 104, 32, 72), placement.Buttons);
	}
}
