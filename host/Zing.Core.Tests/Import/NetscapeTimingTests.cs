using System.Diagnostics;
using Zing.Core.Import;

namespace Zing.Core.Tests.Import;

/// <summary>An unclosed tag used to make the parser scan the rest of the file once per tag: hours for a file at the import size cap.</summary>
[Collection("Timing")]
public sealed class NetscapeTimingTests
{
	[Theory]
	[InlineData("<A HREF=\"https://x/\">")]
	[InlineData("<A HREF=\"https://x/\"")]
	public void StaysLinearOnTenMegabytesOfTagsThatNeverClose(string tag)
	{
		var html = "<!DOCTYPE NETSCAPE-Bookmark-file-1>\n<DL>" + string.Concat(Enumerable.Repeat(tag, ImportFile.MaxBytes / tag.Length));
		var clock = Stopwatch.StartNew();

		NetscapeBookmarks.Parse(html);

		Assert.InRange(clock.ElapsedMilliseconds, 0, 2000);
	}
}
