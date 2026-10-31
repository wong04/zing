using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// A Zing view that, while open, covers everything below a line of the chrome:
/// the stack dropdown (#overlay-stack, below the tab strip) and the tab
/// overview (#overlay-overview, over the page). A click outside its content
/// lands on its backdrop, never on the page beneath. Created on first use.
/// </summary>
public sealed class CoveringLayer(Panel parent, CoreWebView2Environment environment, Bridge bridge, string hash, Func<Rect> content, Func<double> top, Action<RendererView> created)
{
	private Task<RendererView>? _ready;
	private bool _open;

	public void Changed(bool open)
	{
		_open = open;
		if (_ready is null && !_open) return;
		TestHooks.Background(ShowAsync(), hash);
	}

	public void Layout()
	{
		if (Ready() is not { } view) return;
		if (_open)
		{
			var area = content();
			var y = top();
			view.Place(new Rect(0, y, area.Width, Math.Max(0, area.Height - y)));
			Layers.Raise(parent, view.View);
		}
		view.Visible = _open;
	}

	public void Destroy() => Ready()?.Close();

	private RendererView? Ready() => _ready is { IsCompletedSuccessfully: true } ? _ready.Result : null;

	private async Task ShowAsync()
	{
		var view = await (_ready ??= CreateAsync());
		Layout();
		// Keys (arrows, Enter, Escape) go to the layer, not the page underneath.
		if (_open) view.View.Focus(FocusState.Programmatic);
	}

	private async Task<RendererView> CreateAsync()
	{
		var view = await RendererView.CreateAsync(parent, environment, bridge, hash, transparent: true);
		created(view);
		return view;
	}
}
