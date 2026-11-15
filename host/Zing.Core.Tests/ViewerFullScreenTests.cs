using Zing.Core;

namespace Zing.Core.Tests;

public sealed class ViewerFullScreenTests
{
	private static ViewerFullScreen Watching(string page = "PAGE")
	{
		var viewer = new ViewerFullScreen();
		viewer.PageIs(page);
		return viewer;
	}

	[Fact]
	public void Only_a_webview_guest_of_this_page_is_attached_to_and_only_once()
	{
		var viewer = Watching();
		Assert.True(viewer.IsNewGuest("G1", "webview", "PAGE"));
		Assert.False(viewer.IsNewGuest("G1", "webview", "OTHER"));
		Assert.False(viewer.IsNewGuest("G1", "iframe", "PAGE"));
		Assert.False(viewer.IsNewGuest("G1", "webview", null));
		Assert.False(new ViewerFullScreen().IsNewGuest("G1", "webview", "PAGE"));
		viewer.Attached("G1", "S1");
		Assert.False(viewer.IsNewGuest("G1", "webview", "PAGE"));
	}

	[Fact]
	public void The_guest_going_fullscreen_and_back_changes_the_page()
	{
		var viewer = Watching();
		viewer.Attached("G1", "S1");
		Assert.False(viewer.IsFullScreen);
		Assert.True(viewer.Reported("S1", ViewerFullScreen.Binding, "true"));
		Assert.True(viewer.IsFullScreen);
		Assert.Equal(["S1"], viewer.FullScreenSessions);
		Assert.False(viewer.Reported("S1", ViewerFullScreen.Binding, "true"));
		Assert.True(viewer.Reported("S1", ViewerFullScreen.Binding, "false"));
		Assert.False(viewer.IsFullScreen);
	}

	[Fact]
	public void Reports_from_other_sessions_or_bindings_are_not_the_viewer()
	{
		var viewer = Watching();
		viewer.Attached("G1", "S1");
		Assert.False(viewer.Reported("", ViewerFullScreen.Binding, "true"));
		Assert.False(viewer.Reported("S2", ViewerFullScreen.Binding, "true"));
		Assert.False(viewer.Reported("S1", "somethingElse", "true"));
		Assert.False(viewer.IsFullScreen);
	}

	[Fact]
	public void A_new_document_takes_the_page_out_of_its_viewers_fullscreen_and_forgets_them()
	{
		var viewer = Watching();
		viewer.Attached("G1", "S1");
		viewer.Reported("S1", ViewerFullScreen.Binding, "true");
		Assert.True(viewer.Left());
		Assert.False(viewer.IsFullScreen);
		Assert.False(viewer.Reported("S1", ViewerFullScreen.Binding, "true"));
		Assert.True(viewer.IsNewGuest("G1", "webview", "PAGE"));
		Assert.False(viewer.Left());
	}

	[Fact]
	public void A_page_with_two_viewers_is_fullscreen_while_either_is()
	{
		var viewer = Watching();
		viewer.Attached("G1", "S1");
		viewer.Attached("G2", "S2");
		Assert.True(viewer.Reported("S2", ViewerFullScreen.Binding, "true"));
		Assert.False(viewer.Reported("S1", ViewerFullScreen.Binding, "true"));
		Assert.False(viewer.Reported("S2", ViewerFullScreen.Binding, "false"));
		Assert.True(viewer.IsFullScreen);
		Assert.True(viewer.Reported("S1", ViewerFullScreen.Binding, "false"));
	}

	[Fact]
	public void An_unexpected_report_is_an_error()
	{
		var viewer = Watching();
		viewer.Attached("G1", "S1");
		Assert.Throws<ArgumentException>(() => viewer.Reported("S1", ViewerFullScreen.Binding, "yes"));
	}
}
