using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// The stack dropdown's view (#overlay-stack): while open it covers everything
/// under the tab strip, so a click outside the list lands on its backdrop.
/// Created on first use, so a window that never opens a stack pays nothing.
/// </summary>
public sealed class StackLayer(Panel parent, CoreWebView2Environment environment, Bridge bridge, Func<Rect> content, Action<RendererView> created)
{
	private Task<RendererView>? _ready;
	private bool _open;

	public void Changed(StackDropdownState? state)
	{
		_open = state is not null;
		if (_ready is null && !_open) return;
		TestHooks.Background(ShowAsync(), "stack dropdown");
	}

	public void Layout()
	{
		if (Ready() is not { } view) return;
		if (_open)
		{
			var area = content();
			view.Place(new Rect(0, Zing.Core.Layout.TabStripHeight, area.Width, Math.Max(0, area.Height - Zing.Core.Layout.TabStripHeight)));
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
		// Keys (arrows, Enter, Escape) go to the list, not the page underneath.
		if (_open) view.View.Focus(FocusState.Programmatic);
	}

	private async Task<RendererView> CreateAsync()
	{
		var view = await RendererView.CreateAsync(parent, environment, bridge, "overlay-stack", transparent: true);
		created(view);
		return view;
	}
}
