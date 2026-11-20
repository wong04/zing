using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// The find bar, downloads list and permission prompt (pageui-layer.ts): a
/// transparent column at the page's top-right, sized to what the renderer
/// reports and hidden when empty, so it never takes clicks meant for the page.
/// Its view closes with the window's scope, even one still loading.
/// The vault's account picker is its second view (#picker), placed under the sign-in field it is for.
/// The address bar's suggestions are its third (#suggest), hanging under the address field.
/// </summary>
public sealed class PageUiLayer(HostWindow window, WindowScope scope, CoreWebView2Environment environment, Bridge bridge, Func<Rect> content, Func<double> top, Action<RendererView> created, OverlayHost overlays)
{
	private const double PanelWidth = 392;
	private const double PanelMargin = 8;
	private const double PickerMinWidth = 240;
	private const double PickerGap = 2;
	private const double SuggestionsMinWidth = 360;

	private Task<RendererView>? _ready;
	private PageUiState _state = PageUiState.Empty;
	private double _height;
	private bool _wantsFocus;
	private Task<RendererView>? _pickerReady;
	private Zing.Core.Vault.AccountPicker? _picker;
	private double _pickerHeight;
	private Task<RendererView>? _suggestionsReady;
	private AddressSuggestionsState? _suggestions;
	private double _suggestionsHeight;

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
		LayoutSuggestions();
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
		// Never opened and nothing to show: don't pay for a renderer.
		if (_pickerReady is null && picker is null) return;
		TestHooks.Background(ShowPickerAsync(), "account picker");
	}

	public void SetPickerHeight(int height)
	{
		_pickerHeight = Math.Max(0, height);
		LayoutPicker();
	}

	/// <summary>Whether this is the picker's view: its keys are the picker's too.</summary>
	public bool HostsPicker(HostedView view) => _pickerReady is { IsCompletedSuccessfully: true } ready && ReferenceEquals(ready.Result.View, view);

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
		// It never takes the keyboard: its keys are real key presses on the page (MainWindow.PickerKey).
		view.Place(PickerBounds(picker.Rect, picker.Viewport));
		overlays.Show(OverlayLevel.AccountPicker);
		view.Visible = true;
	}

	/// <summary>
	/// Under the field (above it when there's no room below), at least as wide as the field, and always inside the page's
	/// own bounds on both axes, so a page can't place it over Zing's chrome or another pane, whatever rect it reports.
	/// </summary>
	private Rect PickerBounds(Rect field, Rect page)
	{
		var width = Math.Max(1, Math.Min(Math.Max(field.Width, PickerMinWidth), page.Width));
		var height = Math.Max(1, Math.Min(Math.Max(1, _pickerHeight), page.Height));
		var right = page.X + page.Width;
		var bottom = page.Y + page.Height;
		var x = Math.Clamp(field.X, page.X, Math.Max(page.X, right - width));
		var below = field.Y + field.Height + PickerGap;
		var above = field.Y - PickerGap - height;
		var y = below + height <= bottom ? below : above >= page.Y ? above : below;
		y = Math.Clamp(y, page.Y, Math.Max(page.Y, bottom - height));
		return new Rect(x, y, width, height);
	}

	public void SuggestionsChanged(AddressSuggestionsState? state)
	{
		_suggestions = state;
		// Never opened and nothing to show: don't pay for a renderer.
		if (_suggestionsReady is null && state is null) return;
		TestHooks.Background(ShowSuggestionsAsync(), "address suggestions");
	}

	public void SetSuggestionsHeight(int height)
	{
		_suggestionsHeight = Math.Max(0, height);
		LayoutSuggestions();
	}

	private async Task ShowSuggestionsAsync()
	{
		await (_suggestionsReady ??= CreateSuggestionsAsync());
		scope.Checkpoint("address suggestions");
		LayoutSuggestions();
	}

	private async Task<RendererView> CreateSuggestionsAsync()
	{
		var view = await RendererView.CreateAsync(window, scope, environment, bridge, "suggest", overlay: true);
		scope.ThrowIfClosed("address suggestions");
		overlays.Add(OverlayLevel.AddressSuggestions, view.View);
		created(view);
		return view;
	}

	private void LayoutSuggestions()
	{
		if (_suggestionsReady is not { IsCompletedSuccessfully: true } ready) return;
		var view = ready.Result;
		if (_suggestions is not { } suggestions)
		{
			view.Visible = false;
			return;
		}
		// Shown even before its height is known: a hidden view never lays out, so it could never report one.
		// It never takes the keyboard: the address field keeps it, and sends the arrows' choice here.
		view.Place(SuggestionsBounds(suggestions.Field));
		overlays.Show(OverlayLevel.AddressSuggestions);
		view.Visible = true;
	}

	/// <summary>Just under the address field, at least as wide as it, and inside the window's content.</summary>
	private Rect SuggestionsBounds(Rect field)
	{
		var area = content();
		var width = Math.Max(1, Math.Min(Math.Max(field.Width, SuggestionsMinWidth), area.Width));
		var x = Math.Clamp(field.X, 0, Math.Max(0, area.Width - width));
		var y = Math.Clamp(field.Y + field.Height + PickerGap, 0, Math.Max(0, area.Height - 1));
		return new Rect(x, y, width, Math.Max(1, Math.Min(Math.Max(1, _suggestionsHeight), area.Height - y)));
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
