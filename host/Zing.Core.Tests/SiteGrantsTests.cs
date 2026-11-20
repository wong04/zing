using Zing.Core;

namespace Zing.Core.Tests;

/// <summary>"Remember for this site" on a notification prompt: kept on disk until Settings removes it, and only then.</summary>
public sealed class SiteGrantsTests : IDisposable
{
	private sealed class Resolver : IPermissionResolver
	{
		public List<bool> Answers { get; } = [];

		public void Resolve(bool granted) => Answers.Add(granted);
	}

	private const string Url = "https://a.test/page";
	private const string Origin = "https://a.test";

	private readonly string _dir = Directory.CreateTempSubdirectory("zing-site-grants-").FullName;

	private string FilePath => Path.Combine(_dir, SiteGrants.FileName);

	public void Dispose() => Directory.Delete(_dir, recursive: true);

	private (PermissionBroker Broker, PageUiStore Ui) Broker(SiteGrants sites)
	{
		var ui = new PageUiStore();
		return (new PermissionBroker(ui, _ => null, sites: sites), ui);
	}

	private static Resolver Ask(PermissionBroker broker, object page, string permission = "notifications", string url = Url)
	{
		var resolver = new Resolver();
		broker.Request(page, url, permission, new PermissionDetails(), resolver);
		return resolver;
	}

	[Fact]
	public void Nothing_is_written_until_a_site_is_remembered()
	{
		var sites = SiteGrants.Open(FilePath);
		var (broker, ui) = Broker(sites);
		Ask(broker, new object());
		broker.Answer(ui.State.Permission!.Id, true);
		Assert.False(File.Exists(FilePath));
		Assert.Empty(sites.State.Notifications);
	}

	[Fact]
	public void A_remembered_site_is_granted_without_asking_on_any_page_and_after_a_restart()
	{
		var (broker, ui) = Broker(SiteGrants.Open(FilePath));
		var first = Ask(broker, new object());
		broker.RememberForSite(ui.State.Permission!.Id);
		Assert.Equal([true], first.Answers);
		Assert.True(File.Exists(FilePath));

		var (later, laterUi) = Broker(SiteGrants.Open(FilePath));
		var again = Ask(later, new object(), url: "https://a.test/other");
		Assert.Equal([true], again.Answers);
		Assert.Null(laterUi.State.Permission);
		Assert.Equal([Origin], SiteGrants.Open(FilePath).State.Notifications);
	}

	[Fact]
	public void Only_notifications_are_remembered_and_another_site_is_still_asked()
	{
		var sites = SiteGrants.Open(FilePath);
		sites.RememberNotifications(Origin);
		var (broker, ui) = Broker(sites);
		var camera = new Resolver();
		broker.Request(new object(), Url, "media", new PermissionDetails(MediaTypes: ["video"]), camera);
		Assert.Empty(camera.Answers);
		Assert.Throws<NotANotificationPromptException>(() => broker.RememberForSite(ui.State.Permission!.Id));
		broker.Answer(ui.State.Permission!.Id, false);
		var other = Ask(broker, new object(), url: "https://b.test/");
		Assert.Empty(other.Answers);
		Assert.Equal("https://b.test", ui.State.Permission!.Origin);
	}

	[Fact]
	public void A_page_that_isnt_a_web_site_cannot_be_remembered()
	{
		var (broker, ui) = Broker(SiteGrants.Open(FilePath));
		Ask(broker, new object(), url: "file:///C:/page.html");
		Assert.Throws<NotARememberableSiteException>(() => broker.RememberForSite(ui.State.Permission!.Id));
		Assert.False(File.Exists(FilePath));
	}

	[Fact]
	public void Removing_the_last_site_deletes_the_file_and_the_site_is_asked_again()
	{
		var sites = SiteGrants.Open(FilePath);
		sites.RememberNotifications(Origin);
		sites.RememberNotifications("http://127.0.0.1:8080");
		Assert.Equal(["http://127.0.0.1:8080", Origin], sites.State.Notifications);
		var changes = 0;
		sites.Subscribe(() => changes++);
		sites.ForgetNotifications("http://127.0.0.1:8080");
		Assert.True(File.Exists(FilePath));
		sites.ForgetNotifications(Origin);
		Assert.Equal(2, changes);
		Assert.False(File.Exists(FilePath));

		var (broker, ui) = Broker(sites);
		var asked = Ask(broker, new object());
		Assert.Empty(asked.Answers);
		Assert.NotNull(ui.State.Permission);
	}

	[Fact]
	public void The_file_is_written_whole_as_tab_indented_json()
	{
		SiteGrants.Open(FilePath).RememberNotifications(Origin);
		Assert.Equal("{\n\t\"notifications\": [\n\t\t\"https://a.test\"\n\t]\n}\n", File.ReadAllText(FilePath).ReplaceLineEndings("\n"));
		Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
	}

	[Theory]
	[InlineData("not json")]
	[InlineData("{}")]
	[InlineData("{\"notifications\": [\"https://a.test/path\"]}")]
	[InlineData("{\"notifications\": [42]}")]
	public void An_unreadable_file_is_an_error_and_is_left_alone(string text)
	{
		File.WriteAllText(FilePath, text);
		Assert.Throws<SiteGrantsFileException>(() => SiteGrants.Open(FilePath));
		Assert.Equal(text, File.ReadAllText(FilePath));
	}
}
