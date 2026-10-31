namespace Zing.Core.Tests;

public sealed class PermissionsTests
{
	private sealed class Resolver : IPermissionResolver
	{
		public List<bool> Answers { get; } = [];

		public void Resolve(bool granted) => Answers.Add(granted);
	}

	private static (PermissionBroker Broker, PageUiStore Ui, object Page) Setup()
	{
		var ui = new PageUiStore();
		return (new PermissionBroker(ui), ui, new object());
	}

	private const string Url = "https://a.test/";

	[Fact]
	public void What_it_does_not_prompt_for_is_refused()
	{
		var (broker, _, page) = Setup();
		var resolver = new Resolver();
		broker.Request(page, Url, "midi", new PermissionDetails(), resolver);
		Assert.Equal([false], resolver.Answers);
	}

	[Theory]
	[InlineData("fullscreen")]
	[InlineData("clipboard-sanitized-write")]
	[InlineData("pointerLock")]
	public void What_ordinary_pages_need_is_allowed_without_asking(string permission)
	{
		var (broker, ui, page) = Setup();
		var resolver = new Resolver();
		broker.Request(page, Url, permission, new PermissionDetails(), resolver);
		Assert.Equal([true], resolver.Answers);
		Assert.Null(ui.State.Permission);
		Assert.True(broker.Check(null, permission, "https://a.test", null));
	}

	[Fact]
	public void A_prompt_is_held_until_answered_then_remembered_for_that_page()
	{
		var (broker, ui, page) = Setup();
		var first = new Resolver();
		broker.Request(page, Url, "geolocation", new PermissionDetails("https://a.test/x"), first);
		Assert.Empty(first.Answers);
		Assert.Equal(new PermissionPrompt(1, "https://a.test", "location"), ui.State.Permission);
		broker.Answer(ui.State.Permission!.Id, true);
		Assert.Equal([true], first.Answers);
		Assert.Null(ui.State.Permission);

		var second = new Resolver();
		broker.Request(page, Url, "geolocation", new PermissionDetails("https://a.test/y"), second);
		Assert.Equal([true], second.Answers);
		Assert.True(broker.Check(page, "geolocation", "https://a.test", null));
		Assert.False(broker.Check(new object(), "geolocation", "https://a.test", null));
	}

	[Fact]
	public void Deny_is_not_remembered()
	{
		var (broker, ui, page) = Setup();
		var resolver = new Resolver();
		broker.Request(page, Url, "notifications", new PermissionDetails(), resolver);
		broker.Answer(ui.State.Permission!.Id, false);
		Assert.Equal([false], resolver.Answers);
		Assert.False(broker.Check(page, "notifications", "https://a.test", null));
	}

	[Theory]
	[InlineData("video,audio", "camera and microphone")]
	[InlineData("video", "camera")]
	[InlineData("audio", "microphone")]
	public void Media_requests_are_labelled_by_what_they_ask_for(string types, string kind)
	{
		var (broker, ui, page) = Setup();
		broker.Request(page, Url, "media", new PermissionDetails(null, types.Split(',')), new Resolver());
		Assert.Equal(kind, ui.State.Permission?.Kind);
	}

	[Fact]
	public void A_media_request_for_nothing_is_refused()
	{
		var (broker, _, page) = Setup();
		var resolver = new Resolver();
		broker.Request(page, Url, "media", new PermissionDetails(null, []), resolver);
		Assert.Equal([false], resolver.Answers);
	}

	[Fact]
	public void A_camera_grant_does_not_cover_a_later_camera_and_microphone_request()
	{
		var (broker, ui, page) = Setup();
		broker.Request(page, Url, "media", new PermissionDetails(null, ["video"]), new Resolver());
		broker.Answer(ui.State.Permission!.Id, true);
		var both = new Resolver();
		broker.Request(page, Url, "media", new PermissionDetails(null, ["video", "audio"]), both);
		Assert.Empty(both.Answers);
		Assert.True(broker.Check(page, "media", "https://a.test", "video"));
		Assert.False(broker.Check(page, "media", "https://a.test", "audio"));
	}

	[Fact]
	public void Requests_queue_and_show_one_at_a_time()
	{
		var (broker, ui, page) = Setup();
		broker.Request(page, Url, "geolocation", new PermissionDetails(), new Resolver());
		broker.Request(page, Url, "notifications", new PermissionDetails(), new Resolver());
		Assert.Equal("location", ui.State.Permission?.Kind);
		broker.Answer(ui.State.Permission!.Id, false);
		Assert.Equal("notifications", ui.State.Permission?.Kind);
	}

	[Fact]
	public void Navigating_away_refuses_the_pages_pending_prompts_and_forgets_its_grants()
	{
		var (broker, ui, page) = Setup();
		var granted = new Resolver();
		broker.Request(page, Url, "geolocation", new PermissionDetails(), granted);
		broker.Answer(ui.State.Permission!.Id, true);
		var pending = new Resolver();
		var other = new Resolver();
		broker.Request(page, Url, "notifications", new PermissionDetails(), pending);
		var otherPage = new object();
		broker.Request(otherPage, Url, "geolocation", new PermissionDetails(), other);

		broker.DropFor(page);

		Assert.Equal([false], pending.Answers);
		Assert.Empty(other.Answers);
		Assert.False(broker.Check(page, "geolocation", "https://a.test", null));
		Assert.Equal("location", ui.State.Permission?.Kind);
	}

	[Fact]
	public void Answering_a_prompt_that_is_gone_does_nothing()
	{
		var (broker, ui, page) = Setup();
		var resolver = new Resolver();
		broker.Request(page, Url, "geolocation", new PermissionDetails(), resolver);
		var id = ui.State.Permission!.Id;
		broker.DropFor(page);
		broker.Answer(id, true);
		Assert.Equal([false], resolver.Answers);
	}

	[Fact]
	public void A_page_without_a_usable_url_is_named_generically()
	{
		var (broker, ui, page) = Setup();
		broker.Request(page, "", "geolocation", new PermissionDetails(), new Resolver());
		Assert.Equal("this page", ui.State.Permission?.Origin);
	}
}
