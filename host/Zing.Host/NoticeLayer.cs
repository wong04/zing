using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// The top layer for notices (overlay.ts): a transparent renderer view above the
/// pages, hidden whenever it shows nothing and sized to what it shows, so it
/// never takes clicks meant for the page. Its view closes with the window's
/// scope, even one still loading.
/// </summary>
public sealed class NoticeLayer(HostWindow window, WindowScope scope, CoreWebView2Environment environment, Bridge bridge, Func<Rect> content, DispatcherQueue dispatcher, OverlayHost overlays)
{
	private const int NoticeMs = 4000;
	// Long enough to read and choose from; it is a question, not a status.
	private const int OfferMs = 20000;
	private const double NoticeWidth = 480;
	private const double NoticeHeight = 56;
	private const double OfferHeight = 96;
	private const double NoticeMargin = 24;

	private Task<RendererView>? _ready;
	private DispatcherQueueTimer? _hideTimer;
	private int _nextId = 1;
	private double _height = NoticeHeight;

	public Task ShowAsync(string text) => ShowAsync(new { id = _nextId++, text }, NoticeHeight, NoticeMs);

	/// <summary>The offer after a page failed like a block: buttons that open Open a Link or Diagnose. It waits for a click and starts nothing itself.</summary>
	public Task ShowOfferAsync(string address) =>
		ShowAsync(new { id = _nextId++, text = "This page didn't load", offer = new { address } }, OfferHeight, OfferMs);

	private async Task ShowAsync(object notice, double height, int milliseconds)
	{
		var view = await (_ready ??= CreateAsync());
		scope.Checkpoint("overlay");
		_height = height;
		view.Place(Bounds());
		view.Send(Bridge.Event("overlay:notice", notice));
		overlays.Show(OverlayLevel.Notices);
		view.Visible = true;
		_hideTimer ??= CreateTimer();
		_hideTimer.Stop();
		_hideTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
		_hideTimer.Start();
	}

	public void Hide()
	{
		_hideTimer?.Stop();
		if (Ready() is not { } view) return;
		view.Visible = false;
		view.Send(Bridge.Event("overlay:notice", null));
	}

	public void Layout()
	{
		if (Ready() is { Visible: true } view) view.Place(Bounds());
	}

	/// <summary>The window closed: its scope closes the view, and no timer may fire for it.</summary>
	public void Destroy() => _hideTimer?.Stop();

	private RendererView? Ready() => _ready is { IsCompletedSuccessfully: true } ? _ready.Result : null;

	private Rect Bounds()
	{
		var area = content();
		var width = Math.Min(NoticeWidth, area.Width - 2 * NoticeMargin);
		return new Rect(Math.Round((area.Width - width) / 2), area.Height - _height - NoticeMargin, width, _height);
	}

	private async Task<RendererView> CreateAsync()
	{
		var view = await RendererView.CreateAsync(window, scope, environment, bridge, "overlay", overlay: true);
		overlays.Add(OverlayLevel.Notices, view.View);
		return view;
	}

	private DispatcherQueueTimer CreateTimer()
	{
		var timer = dispatcher.CreateTimer();
		timer.IsRepeating = false;
		timer.Tick += (_, _) => Hide();
		return timer;
	}
}

