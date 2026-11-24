using Zing.Core.Mcp;

namespace Zing.Core.Tests.Mcp;

public sealed class CdpKeysTests
{
	[Theory]
	[InlineData("Enter", "Enter", 13, "\r")]
	[InlineData("enter", "Enter", 13, "\r")]
	[InlineData("Tab", "Tab", 9, null)]
	[InlineData("Escape", "Escape", 27, null)]
	[InlineData("Backspace", "Backspace", 8, null)]
	[InlineData("ArrowDown", "ArrowDown", 40, null)]
	[InlineData("PageUp", "PageUp", 33, null)]
	[InlineData(" ", "Space", 32, " ")]
	[InlineData("a", "KeyA", 65, "a")]
	[InlineData("A", "KeyA", 65, "A")]
	[InlineData("7", "Digit7", 55, "7")]
	public void KnownKeys(string key, string code, int virtualKey, string? text)
	{
		var found = CdpKeys.Of(key);
		Assert.Equal(code, found.Code);
		Assert.Equal(virtualKey, found.VirtualKey);
		Assert.Equal(text, found.Text);
	}

	[Theory]
	[InlineData("")]
	[InlineData("Hyper")]
	[InlineData("ab")]
	[InlineData("\r")]
	[InlineData("\n")]
	[InlineData("\t")]
	[InlineData("\u001b")]
	[InlineData("\u007f")]
	public void UnknownKeysThrow(string key) => Assert.Throws<UnknownKeyException>(() => CdpKeys.Of(key));
}
