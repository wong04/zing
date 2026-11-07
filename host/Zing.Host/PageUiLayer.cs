using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// The find bar, downloads list and permission prompt (pageui-layer.ts): a
/// transparent column at the page's top-right, sized to what the renderer
/// reports and hidden when empty, so it never takes clicks meant for the page.
/// Its view closes with the window's scope, even one still loading.
/// The vault's account picker is its second view (#picker), placed under the sign-in field it is for.
/// </summary>
public sealed class PageUiLayer(HostWindow window, WindowScope scope, CoreWebView2Environment environment, Bridge bridge, Func<Rect> content, Func<double> top, Action<RendererView> created, OverlayHost overlays)
{
	private const double PanelWidth = 392;
	private const double PanelMargin = 8;
	private const double PickerMinWidth = 240;
	private const double PickerGap = 2;

	private Task<RendererView>? _ready;
	private PageUiState _state = PageUiState.Empty;
	private double _height;
	private bool _wantsFocus;
	private Task<RendererView>? _pickerReady;
	private Zing.Core.Vault.AccountPicker? _picker;
	private double _pickerHeight;
	private bool _pickerWantsFocus;

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
		LayoutPicker();
		if (Ready() is not { } view) return;
		// Shown even before its height is known: a hidden view never lays out, so it could never report one.
		var shown = _state.HasVisiblePageUi;
		if (shown)
		{
			view.Place(Bounds());
			overlays.Show(OverlayLevel.PageUi);
		}
		view.Visible = shown;
		if (!shown || !_wantsFocus) return;
		_wantsFocus = false;
		view.View.Focus();
	}

	private RendererView? Ready() => _ready is { IsCompletedSuccessfully: true } ? _ready.Result : null;

	private async Task ShowAsync()
	{
		await (_ready ??= CreateAsync());
		scope.Checkpoint("pageui");
		Layout();
	}

	private async Task<RendererView> CreateAsync()
	{
		var view = await RendererView.CreateAsync(window, scope, environment, bridge, "pageui", overlay: true);
		scope.ThrowIfClosed("pageui");
		overlays.Add(OverlayLevel.PageUi, view.View);
		created(view);
		return view;
	}

	public void PickerChanged(Zing.Core.Vault.AccountPicker? picker)
	{
		_picker = picker;
		if (picker is null) _pickerWantsFocus = false;
		// Never opened and nothing to show: don't pay for a renderer.
		if (_pickerReady is null && picker is null) return;
		TestHooks.Background(ShowPickerAsync(), "account picker");
	}

	public void SetPickerHeight(int height)
	{
		_pickerHeight = Math.Max(0, height);
		LayoutPicker();
	}

	/// <summary>The down arrow in the field: the picker takes the keyboard once it shows.</summary>
	public void FocusPicker()
	{
		_pickerWantsFocus = true;
		LayoutPicker();
	}

	private async Task ShowPickerAsync()
	{
		await (_pickerReady ??= CreatePickerAsync());
		scope.Checkpoint("account picker");
		LayoutPicker();
	}

	private async Task<RendererView> CreatePickerAsync()
	{
		var view = await RendererView.CreateAsync(window, scope, environment, bridge, "picker", overlay: true);
		scope.ThrowIfClosed("account picker");
		overlays.Add(OverlayLevel.AccountPicker, view.View);
		created(view);
		return view;
	}

	private void LayoutPicker()
	{
		if (_pickerReady is not { IsCompletedSuccessfully: true } ready) return;
		var view = ready.Result;
		if (_picker is not { } picker)
		{
			view.Visible = false;
			return;
		}
		// Shown even before its height is known: a hidden view never lays out, so it could never report one.
		view.Place(PickerBounds(picker.Rect));
		overlays.Show(OverlayLevel.AccountPicker);
		view.Visible = true;
		if (!_pickerWantsFocus) return;
		_pickerWantsFocus = false;
		view.View.Focus();
	}

	/// <summary>Under the field (above it when there's no room below), at least as wide as the field, inside the content.</summary>
	private Rect PickerBounds(Rect field)
	{
		var area = content();
		var width = Math.Min(Math.Max(field.Width, PickerMinWidth), area.Width);
		var x = Math.Clamp(field.X, 0, Math.Max(0, area.Width - width));
		var height = Math.Max(1, _pickerHeight);
		var below = field.Y + field.Height + PickerGap;
		var above = field.Y - PickerGap - height;
		var y = below + height <= area.Height || above < 0 ? below : above;
		return new Rect(x, y, width, Math.Max(1, Math.Min(height, area.Height - y)));
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
