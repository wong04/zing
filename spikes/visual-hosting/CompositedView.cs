using System.Numerics;
using Microsoft.Web.WebView2.Core;
using Windows.UI.Composition;

/// <summary>A WebView2 hosted as a visual: its own ContainerVisual in the window's tree, placed by the app.</summary>
public sealed class CompositedView
{
	private const string Host = "spike.zing";

	private CompositedView(string name, CoreWebView2CompositionController controller, ContainerVisual visual)
	{
		Name = name;
		Composition = controller;
		Visual = visual;
		Interop = new WebView2Interop(controller);
		UpdateCursor();
	}

	public string Name { get; }
	public CoreWebView2CompositionController Composition { get; }
	public CoreWebView2Controller Controller => Composition;
	public CoreWebView2 Core => Composition.CoreWebView2;
	public ContainerVisual Visual { get; }
	public WebView2Interop Interop { get; }

	/// <summary>Where the view is, in physical client pixels of the window.</summary>
	public Native.RECT Bounds { get; private set; }

	/// <summary>Where the view takes input, in physical pixels relative to the view; null means all of it.</summary>
	public List<Native.RECT>? HitRects { get; set; }

	public nint CursorHandle { get; private set; }
	public string CursorName { get; private set; } = "Arrow";

	public static async Task<CompositedView> CreateAsync(CoreWebView2Environment env, nint hwnd, Compositor compositor, ContainerVisual root, string name, string webFolder, bool transparent)
	{
		var controller = await env.CreateCoreWebView2CompositionControllerAsync(CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)hwnd));
		var visual = compositor.CreateContainerVisual();
		// Top of the tree: views created later draw over views created earlier.
		root.Children.InsertAtTop(visual);
		// WebView2 connects its own visuals under ours. Windows.UI.Composition commits on its own, so there is
		// no device Commit() to call (with DirectComposition there would be).
		controller.RootVisualTarget = visual;
		// The window tells each view its scale on WM_DPICHANGED; views don't watch monitors themselves.
		controller.ShouldDetectMonitorScaleChanges = false;
		if (transparent) controller.DefaultBackgroundColor = Windows.UI.Color.FromArgb(0, 255, 255, 255);
		controller.CoreWebView2.SetVirtualHostNameToFolderMapping(Host, webFolder, CoreWebView2HostResourceAccessKind.Allow);
		return new CompositedView(name, controller, visual);
	}

	public void Navigate(string file) => Core.Navigate($"https://{Host}/{file}");

	public void Place(int x, int y, int width, int height, double scale)
	{
		Bounds = new Native.RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
		Visual.Offset = new Vector3(x, y, 0);
		Visual.Size = new Vector2(width, height);
		Controller.RasterizationScale = scale;
		// As in Microsoft's WebView2APISample (ViewComponent::ResizeWebView): the visual's offset places the
		// pixels, and Bounds carries the same offset so WebView2 can place what it shows outside our tree
		// (<select> popups, context menus, the IME candidate window) at the right screen position.
		Controller.Bounds = new Windows.Foundation.Rect(x, y, width, height);
	}

	public bool Wants(int x, int y)
	{
		if (x < Bounds.Left || x >= Bounds.Right || y < Bounds.Top || y >= Bounds.Bottom) return false;
		if (HitRects is null) return true;
		var (vx, vy) = (x - Bounds.Left, y - Bounds.Top);
		return HitRects.Any(r => vx >= r.Left && vx < r.Right && vy >= r.Top && vy < r.Bottom);
	}

	/// <summary>SendMouseInput and the drag calls take points relative to the view.</summary>
	public Windows.Foundation.Point ToView(Native.POINT client) => new(client.X - Bounds.Left, client.Y - Bounds.Top);

	public Native.POINT ToViewPoint(Native.POINT client) => new() { X = client.X - Bounds.Left, Y = client.Y - Bounds.Top };

	public void Focus() => Controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);

	public void UpdateCursor()
	{
		// The C#/WinRT projection reports a CoreCursor (type + resource id), not an HCURSOR, and has no
		// SystemCursorId. CSS url() cursors come back as Custom and can't be shown this way (README).
		var cursor = Composition.Cursor;
		var type = cursor?.Type ?? Windows.UI.Core.CoreCursorType.Arrow;
		CursorName = type.ToString();
		CursorHandle = Native.LoadCursorW(0, type switch
		{
			Windows.UI.Core.CoreCursorType.Cross => 32515,
			Windows.UI.Core.CoreCursorType.Hand => 32649,
			Windows.UI.Core.CoreCursorType.Help => 32651,
			Windows.UI.Core.CoreCursorType.IBeam => 32513,
			Windows.UI.Core.CoreCursorType.SizeAll => 32646,
			Windows.UI.Core.CoreCursorType.SizeNortheastSouthwest => 32643,
			Windows.UI.Core.CoreCursorType.SizeNorthSouth => 32645,
			Windows.UI.Core.CoreCursorType.SizeNorthwestSoutheast => 32642,
			Windows.UI.Core.CoreCursorType.SizeWestEast => 32644,
			Windows.UI.Core.CoreCursorType.UniversalNo => 32648,
			Windows.UI.Core.CoreCursorType.UpArrow => 32516,
			Windows.UI.Core.CoreCursorType.Wait => 32514,
			Windows.UI.Core.CoreCursorType.Pin => 32671,
			Windows.UI.Core.CoreCursorType.Person => 32672,
			_ => 32512,
		});
	}
}
