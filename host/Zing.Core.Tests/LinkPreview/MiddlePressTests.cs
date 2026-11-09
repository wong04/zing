using Zing.Core.LinkPreview;

namespace Zing.Core.Tests.LinkPreview;

public class MiddlePressTests
{
	[Theory]
	[InlineData(5000L, 5000L, true)]
	[InlineData(5000L, 6000L, true)]
	[InlineData(5000L, 6001L, false)]
	[InlineData(5000L, 4999L, false)]
	public void APressMakesTheRequestsWithinASecond(long pressed, long now, bool made)
	{
		var press = new MiddlePress();
		press.Pressed(pressed);
		Assert.Equal(made, press.Take(now));
	}

	[Fact]
	public void NoPressMakesNone() => Assert.False(new MiddlePress().Take(5000));

	[Fact]
	public void APressMakesOneRequestOnly()
	{
		var press = new MiddlePress();
		press.Pressed(5000);
		Assert.True(press.Take(5100));
		Assert.False(press.Take(5200));
	}

	[Fact]
	public void AStalePressIsUsedUpToo()
	{
		var press = new MiddlePress();
		press.Pressed(5000);
		Assert.False(press.Take(7000));
		press.Pressed(8000);
		Assert.True(press.Take(8500));
	}

	[Fact]
	public void TheLatestPressCounts()
	{
		var press = new MiddlePress();
		press.Pressed(1000);
		press.Pressed(5000);
		Assert.True(press.Take(5900));
	}
}
