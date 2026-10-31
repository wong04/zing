using Zing.Core;

namespace Zing.Core.Tests;

// Mirrors src/shared/browser/textSize.test.ts.
public sealed class TextSizeTests
{
	[Fact]
	public void Steps_up_and_down_from_the_default() => Assert.Equal((110, 90), (TextSize.Bigger(100), TextSize.Smaller(100)));

	[Fact]
	public void Stops_at_the_ends() => Assert.Equal((200, 70), (TextSize.Bigger(200), TextSize.Smaller(70)));

	[Fact]
	public void Treats_an_unknown_value_as_the_default() => Assert.Equal(110, TextSize.Bigger(103));

	[Fact]
	public void Emits_an_important_root_override() => Assert.Contains("font-size: 125% !important", TextSize.Css(125));

	[Fact]
	public void The_default_size_takes_the_override_out() => Assert.Contains("style.textContent = \"\"", TextSize.Script(100));
}
