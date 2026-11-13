using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// A link preview's buttons (#peek): a transparent view in the margin at the top right of the preview's top layer
/// (PeekLayout's Buttons), shown only while a preview is open. The dim and the border around the preview are the
/// scrim's. Made by the first preview, not at start-up: an idle window has no #peek renderer. Its
/// view closes with the window's scope, even one still loading. It never takes the keyboard on showing: the preview's page has it.
/// </summary>
public sealed class PeekControlsLayer(HostWindow window, WindowScope scope, CoreWebView2Environment environment, Bridge bridge, Func<Rect?> buttons, Action<RendererView> created, OverlayHost overlays)
{
	private Task<RendererView>? _ready;
	private bool _open;

	public void Changed(bool open)
	{
		_open = open;
		// Never shown and nothing to show: don't pay for a renderer.
		if (_ready is null && !open) return;
		TestHooks.Background(ShowAsync(), "preview buttons");
	}

	public void Layout()
	{
		if (Ready() is not { } view) return;
		var bounds = _open ? buttons() : null;
		if (bounds is { } rect)
		{
			view.Place(rect);
			overlays.Show(OverlayLevel.PeekControls);
		}
		view.Visible = bounds is not null;
	}

	/// <summary>Whether this is the buttons' view: Esc there closes the preview's top layer, as on its page.</summary>
	public bool Hosts(HostedView view) => Ready() is { } ready && ReferenceEquals(ready.View, view);

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
			// Not kept: the next preview makes the view again rather than failing on this one for the window's life.
			_ready = null;
			throw;
		}
		scope.Checkpoint("preview buttons");
		Layout();
	}

	private async Task<RendererView> CreateAsync()
	{
		var view = await RendererView.CreateAsync(window, scope, environment, bridge, "peek", overlay: true);
		scope.ThrowIfClosed("preview buttons");
		overlays.Add(OverlayLevel.PeekControls, view.View);
		created(view);
		return view;
	}
}
