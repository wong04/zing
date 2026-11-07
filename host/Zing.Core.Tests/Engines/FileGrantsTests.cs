using Zing.Core.Engines;

namespace Zing.Core.Tests.Engines;

public sealed class FileGrantsTests
{
	private const string Zing = "https://app.zing/index.html";

	[Fact]
	public void A_grant_gives_the_renderer_a_name_and_a_token_but_not_the_path()
	{
		var grants = new FileGrants();
		var path = Path.Combine(Path.GetTempPath(), "private folder", "holiday.mov");

		var grant = grants.Issue(path);

		Assert.Equal("holiday.mov", grant.Name);
		Assert.DoesNotContain("private", grant.Token);
		Assert.DoesNotContain("holiday", grant.Token);
		Assert.Equal(path, grants.Resolve(grant.Token));
	}

	[Fact]
	public void Two_grants_of_one_path_have_different_tokens()
	{
		var grants = new FileGrants();
		var path = Path.Combine(Path.GetTempPath(), "a.mov");

		Assert.NotEqual(grants.Issue(path).Token, grants.Issue(path).Token);
	}

	[Theory]
	[InlineData(@"C:\Windows\win.ini")]
	[InlineData("not-a-token")]
	[InlineData("")]
	public void A_path_or_token_nobody_issued_is_refused(string offered)
	{
		Assert.Throws<UnknownFileGrantException>(() => new FileGrants().Resolve(offered));
	}

	[Fact]
	public void The_oldest_grants_are_forgotten_past_the_limit()
	{
		var grants = new FileGrants();
		var first = grants.Issue(Path.Combine(Path.GetTempPath(), "first.mov"));
		for (var i = 0; i < FileGrants.Limit; i++) grants.Issue(Path.Combine(Path.GetTempPath(), $"f{i}.mov"));

		Assert.Throws<UnknownFileGrantException>(() => grants.Resolve(first.Token));
	}

	[Fact]
	public void A_drop_from_a_zing_view_is_granted_once()
	{
		var inbox = new DropInbox(new FileGrants());
		var path = Path.Combine(Path.GetTempPath(), "dropped.png");

		inbox.Offer(Zing, [path]);
		var granted = inbox.Take();

		Assert.Equal(["dropped.png"], granted.Select(g => g.Name));
		Assert.Throws<NoDroppedFilesException>(inbox.Take);
	}

	[Fact]
	public void A_drop_nobody_collected_is_forgotten_by_the_clear_that_follows_every_call()
	{
		var inbox = new DropInbox(new FileGrants());
		inbox.Offer(Zing, [Path.Combine(Path.GetTempPath(), "dropped.png")]);

		inbox.Clear();

		Assert.Throws<NoDroppedFilesException>(inbox.Take);
	}

	[Fact]
	public void Asking_for_a_drop_when_none_arrived_raises()
	{
		Assert.Throws<NoDroppedFilesException>(new DropInbox(new FileGrants()).Take);
	}

	[Theory]
	[InlineData("https://evil.example/")]
	[InlineData("https://app.zing.evil.example/index.html")]
	[InlineData("https://app.zing@evil.example/")]
	[InlineData("http://app.zing/index.html")]
	[InlineData("https://example.com/https://app.zing/")]
	[InlineData("")]
	[InlineData("about:blank")]
	public void A_drop_from_anything_but_a_zing_view_is_refused_and_nothing_is_granted(string source)
	{
		var inbox = new DropInbox(new FileGrants());

		Assert.Throws<UnauthorizedAccessException>(() => inbox.Offer(source, [Path.Combine(Path.GetTempPath(), "x.png")]));
		Assert.Throws<NoDroppedFilesException>(inbox.Take);
	}

	[Theory]
	[InlineData("https://app.zing/index.html", true)]
	[InlineData("https://app.zing/", true)]
	[InlineData("https://app.zing", false)]
	[InlineData("https://APP.ZING/index.html", false)]
	[InlineData("https://evil.example/", false)]
	public void Only_zings_own_origin_counts_as_a_zing_view(string source, bool expected)
	{
		Assert.Equal(expected, ZingOrigin.IsZingView(source));
	}

	[Fact]
	public void File_kinds_name_extensions_and_an_unknown_kind_is_refused()
	{
		Assert.Contains("heic", FileKinds.Extensions(["image"]));
		Assert.Contains("mov", FileKinds.Extensions(["video", "audio"]));
		Assert.Contains("mp3", FileKinds.Extensions(["video", "audio"]));
		Assert.Empty(FileKinds.Extensions(["any"]));
		Assert.Equal(["csv"], FileKinds.Extensions(["csv"]));
		Assert.Throws<UnknownFileKindException>(() => FileKinds.Extensions(["exe-or-whatever"]));
	}
}
