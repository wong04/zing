using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// The round Exit full screen button (#fullscreen-exit), centred near the top of a fullscreen page while the cursor is at
/// the screen's top edge (FullScreenExitReveal). A transparent view exactly the button's size, so it never takes clicks
/// meant for the page. Made when a page first goes fullscreen, not at start-up, and closed with the window's scope, even one still loading.
/// It never takes the keyboard on showing: the fullscreen page has it.
/// </summary>
public sealed class FullScreenExitLayer(HostWindow window, WindowScope scope, CoreWebView2Environment environment, Bridge bridge, Func<Rect> content, Action<RendererView> created, OverlayHost overlays)
{
	// The button is 40 DIPs; the view's 8 px padding (fullscreen.css) leaves room for its shadow and focus ring.
	private const double Size = 56;
	private const double Top = 8;

	private Task<RendererView>? _ready;
	private bool _shown;

	/// <summary>A page went fullscreen: the view is made now, hidden, so the button shows at once when the cursor reaches the top.</summary>
	public void Prepare()
	{
		if (_ready is null) TestHooks.Background(ShowAsync(), "exit full screen button");
	}

	public void Changed(bool shown)
	{
		_shown = shown;
		// Never shown and nothing to show: don't pay for a renderer.
		if (_ready is null && !shown) return;
		TestHooks.Background(ShowAsync(), "exit full screen button");
	}

	public void Layout()
	{
		if (Ready() is not { } view) return;
		if (_shown)
		{
			view.Place(Bounds());
			overlays.Show(OverlayLevel.FullScreenExit);
		}
		view.Visible = _shown;
	}

	private Rect Bounds()
	{
		var area = content();
		return new Rect(Math.Round((area.Width - Size) / 2), Top, Size, Size);
	}

	private RendererView? Ready() => _ready is { IsCompletedSuccessfully: true } ? _ready.Result : null;

	private async Task ShowAsync()
	{
		var ready = _ready ??= CreateAsync();
		try
		{
			await ready;
		}
		catch (Exception) when (_ready == ready)
		{
			// Not kept: the next reveal makes the view again rather than failing on this one for the window's life.
			_ready = null;
			throw;
		}
		scope.Checkpoint("exit full screen button");
		Layout();
	}

	private async Task<RendererView> CreateAsync()
	{
		var view = await RendererView.CreateAsync(window, scope, environment, bridge, "fullscreen-exit", overlay: true);
		scope.ThrowIfClosed("exit full screen button");
		overlays.Add(OverlayLevel.FullScreenExit, view.View);
		created(view);
		return view;
	}
}
