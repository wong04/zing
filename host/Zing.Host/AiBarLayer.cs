using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// A bar of AI control's (the strip, the confirm bar): a transparent renderer view centred at the top of the page, sized to
/// what the view measured and shown only while it has something to show, so it never takes clicks meant for the page
/// around it. The view is made only once there is something to show, and closes with the window's scope, even one still loading.
/// </summary>
public abstract class AiBarLayer(HostWindow window, WindowScope scope, CoreWebView2Environment environment, Bridge bridge, Func<Rect> page, Func<double> top, Action<RendererView> created, OverlayHost overlays, string hash, OverlayLevel level)
{
	private Task<RendererView>? _ready;
	private bool _shown;
	private double _width;
	private double _height;

	/// <summary>Where the bar ends, while it shows; the confirm bar goes under the strip.</summary>
	public double? Bottom => Visible ? top() + _height : null;

	private bool Visible => _shown && _width > 0 && _height > 0 && Ready() is not null;

	public void Changed(bool shown)
	{
		_shown = shown;
		// Never shown and nothing to show: don't pay for a renderer.
		if (_ready is null && !shown) return;
		TestHooks.Background(ShowAsync(), hash);
	}

	/// <summary>The view measured the bar; nothing shows until it has.</summary>
	public void SetSize(int width, int height)
	{
		_width = width;
		_height = height;
		Layout();
	}

	public void Layout()
	{
		if (Ready() is not { } view) return;
		// Shown before its size is known would be a zero-size view, which never lays out: it is placed at its last size or a line's.
		var visible = _shown;
		if (visible)
		{
			view.Place(Bounds());
			overlays.Show(level);
		}
		view.Visible = visible;
	}

	private RendererView? Ready() => _ready is { IsCompletedSuccessfully: true } ? _ready.Result : null;

	private Rect Bounds()
	{
		var area = page();
		var width = Math.Min(_width > 0 ? _width : area.Width, area.Width);
		var height = _height > 0 ? _height : 48;
		return new Rect(Math.Round(area.X + (area.Width - width) / 2), top(), width, height);
	}

	private async Task ShowAsync()
	{
		var ready = _ready ??= CreateAsync();
		try
		{
			await ready;
		}
		catch (Exception) when (_ready == ready)
		{
			// Not kept: the next change makes the view again rather than failing on this one for the window's life.
			_ready = null;
			throw;
		}
		scope.Checkpoint(hash);
		Layout();
	}

	private async Task<RendererView> CreateAsync()
	{
		var view = await RendererView.CreateAsync(window, scope, environment, bridge, hash, overlay: true);
		scope.ThrowIfClosed(hash);
		overlays.Add(level, view.View);
		created(view);
		return view;
	}
}
