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
public sealed class NoticeLayer(HostWindow window, WindowScope scope, CoreWebView2Environment environment, Bridge bridge, Func<Rect> content, DispatcherQueue dispatcher)
{
	private const int NoticeMs = 4000;
	private const double NoticeWidth = 480;
	private const double NoticeHeight = 56;
	private const double NoticeMargin = 24;

	private Task<RendererView>? _ready;
	private DispatcherQueueTimer? _hideTimer;
	private int _nextId = 1;

	public async Task ShowAsync(string text)
	{
		var view = await (_ready ??= CreateAsync());
		scope.Checkpoint("overlay");
		view.Place(Bounds());
		view.Send(Bridge.Event("overlay:notice", new { id = _nextId++, text }));
		view.View.Raise();
		view.Visible = true;
		_hideTimer ??= CreateTimer();
		_hideTimer.Stop();
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

	public void Raise()
	{
		if (Ready() is { } view) view.View.Raise();
	}

	/// <summary>The window closed: its scope closes the view, and no timer may fire for it.</summary>
	public void Destroy() => _hideTimer?.Stop();

	private RendererView? Ready() => _ready is { IsCompletedSuccessfully: true } ? _ready.Result : null;

	private Rect Bounds()
	{
		var area = content();
		var width = Math.Min(NoticeWidth, area.Width - 2 * NoticeMargin);
		return new Rect(Math.Round((area.Width - width) / 2), area.Height - NoticeHeight - NoticeMargin, width, NoticeHeight);
	}

	private Task<RendererView> CreateAsync() => RendererView.CreateAsync(window, scope, environment, bridge, "overlay", overlay: true);

	private DispatcherQueueTimer CreateTimer()
	{
		var timer = dispatcher.CreateTimer();
		timer.Interval = TimeSpan.FromMilliseconds(NoticeMs);
		timer.IsRepeating = false;
		timer.Tick += (_, _) => Hide();
		return timer;
	}
}

