using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// The find bar, downloads list and permission prompt (pageui-layer.ts): a
/// transparent column at the page's top-right, sized to what the renderer
/// reports and hidden when empty, so it never takes clicks meant for the page.
/// </summary>
public sealed class PageUiLayer(HostWindow window, CoreWebView2Environment environment, Bridge bridge, Func<Rect> content, Func<double> top, Action<RendererView> created)
{
	private const double PanelWidth = 392;
	private const double PanelMargin = 8;

	private Task<RendererView>? _ready;
	private PageUiState _state = PageUiState.Empty;
	private double _height;
	private bool _wantsFocus;

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

	/// <summary>Focuses the layer once it shows: the find bar asks before its view has appeared, and a hidden window can't take the keyboard.</summary>
	public void Focus()
	{
		_wantsFocus = true;
		Layout();
	}

	public void Layout()
	{
		if (Ready() is not { } view) return;
		// Shown even before its height is known: a hidden view never lays out, so it could never report one.
		var shown = _state.HasVisiblePageUi;
		if (shown)
		{
			view.Place(Bounds());
			view.View.Raise();
		}
		view.Visible = shown;
		if (!shown || !_wantsFocus) return;
		_wantsFocus = false;
		view.View.Focus();
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
		var view = await RendererView.CreateAsync(window, environment, bridge, "pageui", overlay: true);
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
