using System.Text.Json;
using Zing.Core;

namespace Zing.Core.Tests;

public sealed class LaunchArgsTests
{
	public static TheoryData<string> Names() => [.. Vectors.Load("host/Zing.Core.Tests/vectors/launch-args.vectors.json").EnumerateArray().Select(v => v.GetProperty("name").GetString()!)];

	[Theory]
	[MemberData(nameof(Names))]
	public void Launch_urls_match_the_typescript(string name)
	{
		var vector = Vectors.Load("host/Zing.Core.Tests/vectors/launch-args.vectors.json").EnumerateArray().Single(v => v.GetProperty("name").GetString() == name);
		var argv = vector.GetProperty("argv").EnumerateArray().Select(a => a.GetString()!);
		Assert.Equal(vector.GetProperty("urls").EnumerateArray().Select(u => u.GetString()!), LaunchArgs.Urls(argv));
	}

	[Fact]
	public void A_raw_command_line_splits_like_the_c_runtime()
	{
		Assert.Equal([@"C:\Program Files\Zing\Zing.exe", "https://a.test/?x=1&y=2", "--flag"], LaunchArgs.Split(@"""C:\Program Files\Zing\Zing.exe"" ""https://a.test/?x=1&y=2""  --flag"));
		Assert.Empty(LaunchArgs.Split("   "));
	}

	[Fact]
	public void The_inbox_queues_links_until_a_window_takes_them_then_hands_them_straight_over()
	{
		var inbox = new LinkInbox();
		inbox.Push("https://a.test");
		inbox.Push("https://b.test");
		Assert.Equal(2, inbox.PendingCount);
		var seen = new List<string>();
		inbox.Attach(seen.Add);
		inbox.Push("https://c.test");
		Assert.Equal(["https://a.test", "https://b.test", "https://c.test"], seen);
		Assert.Equal(0, inbox.PendingCount);
	}
}
