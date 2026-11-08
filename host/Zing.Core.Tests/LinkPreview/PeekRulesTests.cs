using Zing.Core.LinkPreview;

namespace Zing.Core.Tests.LinkPreview;

public class PeekRulesTests
{
	private static readonly PeekSettings On = new(true, true, true);
	private static readonly Modifiers None = new(false, false, false);
	private const string Gmail = "https://mail.google.com/mail/u/0/";
	private const string Docs = "https://docs.google.com/document/d/x";

	private static LinkRequest Click(string from, string to, Modifiers? m = null, bool fromPreview = false) =>
		new(from, to, LinkTrigger.Navigation, NavigationCause.AnchorClick, m ?? None, false, fromPreview);

	private static LinkRequest Window(string from, string to, Modifiers? m = null, bool popup = false) =>
		new(from, to, LinkTrigger.NewWindow, NavigationCause.Other, m ?? None, popup, false);

	public static TheoryData<string, LinkRequest, PeekSettings, PeekVerdict> Rows()
	{
		var rows = new TheoryData<string, LinkRequest, PeekSettings, PeekVerdict>();
		void Add(string name, LinkRequest r, PeekVerdict expected, PeekSettings? s = null) => rows.Add(name, r, s ?? On, expected);

		Add("gmail to docs click", Click(Gmail, Docs), PeekVerdict.Preview);
		Add("same host click", Click("https://github.com/a", "https://github.com/b"), PeekVerdict.Normal);
		Add("www ignored", Click("https://bbc.co.uk/", "https://www.bbc.co.uk/news"), PeekVerdict.Normal);
		Add("script navigation", new LinkRequest(Gmail, Docs, LinkTrigger.Navigation, NavigationCause.Other, None, false, false), PeekVerdict.Normal);
		Add("new window cross host", Window(Gmail, Docs), PeekVerdict.Preview);
		Add("google url wrapper", Window(Gmail, "https://www.google.com/url?q=https%3A%2F%2Fdocs.google.com%2Fx"), PeekVerdict.Preview);
		Add("google url wrapper to same host", Window("https://docs.google.com/a", "https://www.google.com/url?url=https%3A%2F%2Fdocs.google.com%2Fx"), PeekVerdict.Normal);
		Add("new window same host", Window("https://a.test/x", "https://a.test/y"), PeekVerdict.Normal);
		Add("new window same host shift", Window("https://a.test/x", "https://a.test/y", new(true, false, false)), PeekVerdict.Preview);
		Add("new window same host shift off", Window("https://a.test/x", "https://a.test/y", new(true, false, false)), PeekVerdict.Normal, new(true, false, true));
		Add("new window ctrl", Window(Gmail, Docs, new(false, true, false)), PeekVerdict.Normal);
		Add("new window middle", Window(Gmail, Docs, new(false, false, true)), PeekVerdict.Normal);
		Add("new window popup features", Window(Gmail, Docs, popup: true), PeekVerdict.Normal);
		Add("new window about:blank", Window(Gmail, "about:blank"), PeekVerdict.Undecided);
		Add("new windows off", Window(Gmail, Docs), PeekVerdict.Normal, new(true, true, false));
		Add("disabled", Click(Gmail, Docs), PeekVerdict.Normal, new(false, true, true));
		Add("disabled shift", Click(Gmail, Docs, new(true, false, false)), PeekVerdict.Normal, new(false, true, true));

		foreach (var to in new[]
		{
			"https://accounts.google.com/signin", "https://login.microsoftonline.com/x", "https://login.live.com/x",
			"https://appleid.apple.com/x", "https://github.com/login", "https://github.com/login/oauth/authorize?x",
		})
			Add($"sign-in {to}", Click("https://example.com/", to), PeekVerdict.Normal);
		Add("github profile", Click("https://example.com/", "https://github.com/wong04"), PeekVerdict.Preview);
		Add("github loginx is not sign-in", Click("https://example.com/", "https://github.com/loginx"), PeekVerdict.Preview);

		foreach (var to in new[] { "https://meet.google.com/abc", "https://us02web.zoom.us/j/1", "https://zoom.us/j/1", "https://teams.microsoft.com/l/x", "https://teams.live.com/x" })
			Add($"meeting {to}", Click("https://example.com/", to), PeekVerdict.Normal);
		Add("notzoom.us is not zoom", Click("https://example.com/", "https://notzoom.us/"), PeekVerdict.Preview);

		foreach (var from in new[]
		{
			"https://www.google.com/search?q=x", "https://google.co.uk/search?q=x", "https://duckduckgo.com/?q=x",
			"https://bing.com/search?q=x", "https://search.brave.com/search?q=x", "https://kagi.com/search?q=x",
			"https://startpage.com/sp/search", "https://www.startpage.com/do/search",
		})
		{
			Add($"search source {from}", Click(from, "https://example.com"), PeekVerdict.Normal);
		}
		Add("search source new window", Window("https://www.google.com/search?q=x", "https://example.com"), PeekVerdict.Normal);
		Add("search source new window shift", Window("https://www.google.com/search?q=x", "https://example.com", new(true, false, false)), PeekVerdict.Preview);
		Add("duckduckgo home is not results", Click("https://duckduckgo.com/", "https://example.com"), PeekVerdict.Preview);
		Add("google maps", Click("https://www.google.com/maps", "https://example.com"), PeekVerdict.Preview);

		foreach (var to in new[] { "mailto:a@b.c", "javascript:void 0", "blob:https://x.test/1", "data:text/html,hi", "zing://settings", "https://app.zing/settings" })
			Add($"non-http to {to}", Click("https://example.com/", to), PeekVerdict.Normal);
		Add("zing from", Click("zing://newtab", "https://example.com"), PeekVerdict.Normal);
		Add("zing origin from", Click("https://app.zing/newtab", "https://example.com"), PeekVerdict.Normal);

		Add("fragment only", Click("https://example.com/a#x", "https://example.com/a#y"), PeekVerdict.Normal);
		Add("fragment only cross host impossible, new window", Window("https://example.com/a", "https://example.com/a#y"), PeekVerdict.Normal);
		Add("from preview", Click(Gmail, Docs, fromPreview: true), PeekVerdict.Normal);
		Add("from preview shift", Click(Gmail, Docs, new(true, false, false), fromPreview: true), PeekVerdict.Normal);
		return rows;
	}

	[Theory]
	[MemberData(nameof(Rows))]
	public void Decides(string name, LinkRequest req, PeekSettings s, PeekVerdict expected)
	{
		Assert.Equal(expected, PeekRules.Decide(req, s));
		Assert.False(string.IsNullOrEmpty(name));
	}

	[Theory]
	[InlineData("https://www.google.com/url?q=https%3A%2F%2Fa.test%2Fx%3Fy%3D1&sa=D", "https://a.test/x?y=1")]
	[InlineData("https://google.co.uk/url?url=https%3A%2F%2Fa.test%2F", "https://a.test/")]
	[InlineData("https://www.google.com/url?q=javascript:alert(1)", "https://www.google.com/url?q=javascript:alert(1)")]
	[InlineData("https://www.google.com/url?sa=D", "https://www.google.com/url?sa=D")]
	[InlineData("https://example.com/url?q=https%3A%2F%2Fa.test", "https://example.com/url?q=https%3A%2F%2Fa.test")]
	[InlineData("not a url", "not a url")]
	public void Unwraps(string url, string expected) => Assert.Equal(expected, PeekRules.Unwrap(url));

	[Theory]
	[InlineData("https://WWW.Example.com/a", "https://example.com/b", true)]
	[InlineData("https://www.www.example.com/", "https://www.example.com/", false)]
	[InlineData("https://a.example.com/", "https://example.com/", false)]
	public void SameHostIgnoresCaseAndOneWww(string a, string b, bool expected) => Assert.Equal(expected, PeekRules.SameHost(a, b));
}
