using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// The find bar, downloads list and permission prompt (pageui-layer.ts): a
/// transparent column at the page's top-right, sized to what the renderer
/// reports and hidden when empty, so it never takes clicks meant for the page.
/// </summary>
public sealed class PageUiLayer(Panel parent, CoreWebView2Environment environment, Bridge bridge, Func<Rect> content, Func<double> top, Action<RendererView> created)
{
	private const double PanelWidth = 392;
	private const double PanelMargin = 8;

	private Task<RendererView>? _ready;
	private PageUiState _state = PageUiState.Empty;
	private double _height;

	public void Changed(PageUiState state)
	{
		_state = state;
		// Nothing to show and no view yet: don't pay for a renderer.
		if (_ready is null && !state.HasVisiblePageUi) return;
		TestHooks.Background(ShowAsync(), "pageui");
	}

	public void SetHeight(int height)
	{
		_height = Math.Max(0, height);
		Layout();
	}

	public void Focus() => Ready()?.View.Focus(FocusState.Programmatic);

	public void Layout()
	{
		if (Ready() is not { } view) return;
		// Shown even before its height is known: a hidden view never lays out, so it could never report one.
		var shown = _state.HasVisiblePageUi;
		if (shown)
		{
			view.Place(Bounds());
			Layers.Raise(parent, view.View);
		}
		view.Visible = shown;
	}

	public void Destroy() => Ready()?.Close();

	private RendererView? Ready() => _ready is { IsCompletedSuccessfully: true } ? _ready.Result : null;

	private async Task ShowAsync()
	{
		await (_ready ??= CreateAsync());
		Layout();
	}

	private async Task<RendererView> CreateAsync()
	{
		var view = await RendererView.CreateAsync(parent, environment, bridge, "pageui", transparent: true);
		created(view);
		return view;
	}

	private Rect Bounds()
	{
		var area = content();
		var width = Math.Min(PanelWidth, area.Width);
		var y = top() + PanelMargin;
		var available = Math.Max(0, area.Height - y);
		return new Rect(area.Width - width, y, width, Math.Max(1, Math.Min(_height, available)));
	}
}
