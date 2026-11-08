using Zing.Core.LinkPreview;

namespace Zing.Core.Tests.LinkPreview;

public class PeekSessionTests
{
	private static IPage NewPage() => new FakePageHost().Create(null!);

	private static PeekSession WithLayers(int count)
	{
		var session = new PeekSession("tab");
		for (var i = 0; i < count; i++) session.Push(NewPage(), false);
		return session;
	}

	[Fact]
	public void PushingPopupsAboveTheCapIsAllowed()
	{
		var session = WithLayers(PeekSession.LayerCap);
		session.Push(NewPage(), true);
		Assert.Equal(PeekSession.LayerCap + 1, session.Layers.Count);
	}

	[Fact]
	public void PushingAPageAboveTheCapThrows()
	{
		var session = WithLayers(PeekSession.LayerCap);
		Assert.Throws<PeekLayerCapException>(() => session.Push(NewPage(), false));
	}

	[Fact]
	public void CanExpandIsFalseUnderAPopup()
	{
		var session = WithLayers(1);
		Assert.True(session.CanExpand);
		session.Push(NewPage(), true);
		Assert.False(session.CanExpand);
	}

	[Fact]
	public void PopRemovesTheTopOnly()
	{
		var session = WithLayers(2);
		var top = session.Top;
		Assert.Same(top, session.Pop());
		Assert.Single(session.Layers);
	}

	[Fact]
	public void PopThrowsWhenOneLayerIsLeft()
	{
		var session = WithLayers(1);
		Assert.Throws<InvalidOperationException>(session.Pop);
	}

	[Fact]
	public void CloseAllReturnsTopFirst()
	{
		var session = WithLayers(3);
		var bottomFirst = session.Layers.ToList();
		var closed = session.CloseAll();
		Assert.Equal(Enumerable.Reverse(bottomFirst), closed);
		Assert.Empty(session.Layers);
	}

	[Fact]
	public void PendingPopupWaitsForARealDocument()
	{
		var seen = new List<string>();
		var popup = new PendingPopup(NewPage(), "tab", url =>
		{
			seen.Add(url);
			return PeekVerdict.Preview;
		});
		Assert.Null(popup.Navigating("about:blank"));
		Assert.Equal(PeekVerdict.Preview, popup.Navigating("https://x.test"));
		Assert.Equal(["https://x.test"], seen);
	}
}
