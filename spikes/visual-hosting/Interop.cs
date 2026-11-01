using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Windows.UI.Composition;

/// <summary>Windows.UI.Composition in a plain Win32 window: the dispatcher queue and the window target.</summary>
internal static class Composition
{
	public static Windows.System.DispatcherQueueController CreateDispatcherQueueOnCurrentThread()
	{
		var options = new Native.DispatcherQueueOptions { dwSize = Marshal.SizeOf<Native.DispatcherQueueOptions>(), threadType = 2, apartmentType = 2 }; // DQTYPE_THREAD_CURRENT, DQTAT_COM_STA
		Marshal.ThrowExceptionForHR(Native.CreateDispatcherQueueController(options, out var controller));
		return Windows.System.DispatcherQueueController.FromAbi(controller);
	}

	/// <summary>ICompositorDesktopInterop::CreateDesktopWindowTarget: the compositor draws into the HWND's client area.</summary>
	public static unsafe Windows.UI.Composition.Desktop.DesktopWindowTarget CreateDesktopWindowTarget(Compositor compositor, nint hwnd)
	{
		var interop = Native.Query(((WinRT.IWinRTObject)compositor).NativeObject.ThisPtr, new Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"));
		try
		{
			nint target;
			var create = (delegate* unmanaged[Stdcall]<nint, nint, int, nint*, int>)(*(nint**)interop)[3];
			Marshal.ThrowExceptionForHR(create(interop, hwnd, 0, &target));
			return Windows.UI.Composition.Desktop.DesktopWindowTarget.FromAbi(target);
		}
		finally
		{
			Marshal.Release(interop);
		}
	}
}

/// <summary>
/// The COM side of a composition controller that the C#/WinRT projection doesn't expose:
/// ICoreWebView2CompositionControllerInterop2 (WebView2Interop.idl in the WebView2 package), implemented by
/// the CoreWebView2CompositionController runtime class. Slots: 3 get_AutomationProvider,
/// 4 get_RootVisualTarget, 5 put_RootVisualTarget, 6 DragEnter, 7 DragLeave, 8 DragOver, 9 Drop.
/// </summary>
public sealed unsafe class WebView2Interop
{
	private static readonly Guid Interop2 = new("6b47bbe1-2480-4ff8-a5ba-69c2f0b868b3");
	private static readonly Guid RawElementProviderSimple = new("d6dd68d1-86fd-4332-8666-9abedea2d24c");
	private readonly nint _ptr;

	public WebView2Interop(CoreWebView2CompositionController controller)
	{
		_ptr = Native.Query(((WinRT.IWinRTObject)controller).NativeObject.ThisPtr, Interop2);
	}

	private nint Slot(int index) => (*(nint**)_ptr)[index];

	/// <summary>The view's IRawElementProviderSimple (AddRef'd), or 0.</summary>
	public nint AutomationProviderSimple()
	{
		nint unknown;
		if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(3))(_ptr, &unknown) != 0 || unknown == 0) return 0;
		try
		{
			return Marshal.QueryInterface(unknown, in RawElementProviderSimple, out var simple) == 0 ? simple : 0;
		}
		finally
		{
			Marshal.Release(unknown);
		}
	}

	public uint DragEnter(nint data, uint keyState, Native.POINT point, uint effect)
	{
		var hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint, Native.POINT, uint*, int>)Slot(6))(_ptr, data, keyState, point, &effect);
		return hr == 0 ? effect : 0;
	}

	public void DragLeave() => ((delegate* unmanaged[Stdcall]<nint, int>)Slot(7))(_ptr);

	public uint DragOver(uint keyState, Native.POINT point, uint effect)
	{
		var hr = ((delegate* unmanaged[Stdcall]<nint, uint, Native.POINT, uint*, int>)Slot(8))(_ptr, keyState, point, &effect);
		return hr == 0 ? effect : 0;
	}

	public uint Drop(nint data, uint keyState, Native.POINT point, uint effect)
	{
		var hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint, Native.POINT, uint*, int>)Slot(9))(_ptr, data, keyState, point, &effect);
		return hr == 0 ? effect : 0;
	}
}

/// <summary>
/// The window's OLE IDropTarget, as a hand-built COM object (a vtable of [UnmanagedCallersOnly] functions),
/// so it doesn't depend on which ComWrappers instance the runtime marshals with. It lives as long as the
/// window, so AddRef/Release only count.
/// </summary>
public sealed unsafe class DropTarget
{
	private static readonly Guid Unknown = new("00000000-0000-0000-C000-000000000046");
	private static readonly Guid IDropTarget = new("00000122-0000-0000-C000-000000000046");
	private static readonly nint* Vtable = CreateVtable();
	private readonly GCHandle _self;

	public DropTarget(SpikeWindow window)
	{
		Window = window;
		_self = GCHandle.Alloc(this);
		Pointer = (nint)NativeMemory.Alloc((nuint)(2 * sizeof(nint)));
		((nint*)Pointer)[0] = (nint)Vtable;
		((nint*)Pointer)[1] = GCHandle.ToIntPtr(_self);
	}

	public SpikeWindow Window { get; }

	/// <summary>The IDropTarget* to hand to RegisterDragDrop.</summary>
	public nint Pointer { get; }

	private static nint* CreateVtable()
	{
		var v = (nint*)NativeMemory.Alloc((nuint)(7 * sizeof(nint)));
		v[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterface;
		v[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
		v[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
		v[3] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, Native.POINT, uint*, int>)&DragEnter;
		v[4] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, Native.POINT, uint*, int>)&DragOver;
		v[5] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&DragLeave;
		v[6] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, Native.POINT, uint*, int>)&Drop;
		return v;
	}

	private static SpikeWindow Of(nint self) => ((DropTarget)GCHandle.FromIntPtr(((nint*)self)[1]).Target!).Window;

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
	private static int QueryInterface(nint self, Guid* iid, nint* result)
	{
		if (*iid == Unknown || *iid == IDropTarget)
		{
			*result = self;
			return 0;
		}
		*result = 0;
		return unchecked((int)0x80004002); // E_NOINTERFACE
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
	private static uint AddRef(nint self) => 1;

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
	private static uint Release(nint self) => 1;

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
	private static int DragEnter(nint self, nint data, uint keyState, Native.POINT point, uint* effect) => Guard(() => *effect = Of(self).DragEnter(data, keyState, point, *effect), effect);

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
	private static int DragOver(nint self, uint keyState, Native.POINT point, uint* effect) => Guard(() => *effect = Of(self).DragOver(keyState, point, *effect), effect);

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
	private static int DragLeave(nint self) => Guard(() => Of(self).DragLeave(), null);

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
	private static int Drop(nint self, nint data, uint keyState, Native.POINT point, uint* effect) => Guard(() => *effect = Of(self).Drop(data, keyState, point, *effect), effect);

	/// <summary>Nothing may unwind into OLE: log, refuse the drop, and return S_OK.</summary>
	private static int Guard(Action action, uint* effect)
	{
		try
		{
			action();
		}
		catch (Exception e)
		{
			SpikeWindow.Write($"drag-drop failed: {e}");
			if (effect != null) *effect = 0;
		}
		return 0;
	}
}

public static class Native
{
	public delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

	[StructLayout(LayoutKind.Sequential)]
	public struct POINT { public int X, Y; }

	[StructLayout(LayoutKind.Sequential)]
	public struct RECT { public int Left, Top, Right, Bottom; }

	[StructLayout(LayoutKind.Sequential)]
	public struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public POINT pt; public uint lPrivate; }

	[StructLayout(LayoutKind.Sequential)]
	public struct DispatcherQueueOptions { public int dwSize, threadType, apartmentType; }

	[StructLayout(LayoutKind.Sequential)]
	public struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public nint hwndTrack; public uint dwHoverTime; }

	[StructLayout(LayoutKind.Sequential)]
	public struct BITMAPINFOHEADER
	{
		public uint biSize;
		public int biWidth, biHeight;
		public ushort biPlanes, biBitCount;
		public uint biCompression, biSizeImage;
		public int biXPelsPerMeter, biYPelsPerMeter;
		public uint biClrUsed, biClrImportant;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	public struct WNDCLASSEXW
	{
		public uint cbSize, style;
		public nint lpfnWndProc;
		public int cbClsExtra, cbWndExtra;
		public nint hInstance, hIcon, hCursor, hbrBackground;
		public string? lpszMenuName;
		public string lpszClassName;
		public nint hIconSm;
	}

	public static nint Query(nint unknown, Guid iid)
	{
		Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var result));
		return result;
	}

	public static void AddRef(nint unknown)
	{
		if (unknown != 0) Marshal.AddRef(unknown);
	}

	public static int RegisterDragDrop(nint hwnd, DropTarget target) => RegisterDragDrop(hwnd, target.Pointer);

	/// <summary>The window's client pixel as the compositor drew it (PrintWindow with PW_RENDERFULLCONTENT).</summary>
	public static unsafe (int R, int G, int B) ClientPixel(nint hwnd, int x, int y)
	{
		GetClientRect(hwnd, out var client);
		var (width, height) = (client.Right, client.Bottom);
		if (x < 0 || y < 0 || x >= width || y >= height) return (-1, -1, -1);
		var screen = GetDC(0);
		var dc = CreateCompatibleDC(screen);
		var header = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
		var bitmap = CreateDIBSection(dc, ref header, 0, out var bits, 0, 0);
		var old = SelectObject(dc, bitmap);
		try
		{
			const uint PW_CLIENTONLY = 1, PW_RENDERFULLCONTENT = 2;
			if (!PrintWindow(hwnd, dc, PW_CLIENTONLY | PW_RENDERFULLCONTENT)) return (-1, -1, -1);
			var pixel = ((byte*)bits) + ((y * width) + x) * 4; // BGRA
			return (pixel[2], pixel[1], pixel[0]);
		}
		finally
		{
			SelectObject(dc, old);
			DeleteObject(bitmap);
			DeleteDC(dc);
			ReleaseDC(0, screen);
		}
	}

	[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
	[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern nint CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
	[DllImport("user32.dll")] public static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
	[DllImport("user32.dll")] public static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
	[DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
	[DllImport("user32.dll")] public static extern nint DispatchMessageW(ref MSG msg);
	[DllImport("user32.dll")] public static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
	[DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
	[DllImport("user32.dll")] public static extern nint LoadCursorW(nint instance, nint name);
	[DllImport("user32.dll")] public static extern nint SetCursor(nint cursor);
	[DllImport("user32.dll")] public static extern nint SetCapture(nint hwnd);
	[DllImport("user32.dll")] public static extern bool ReleaseCapture();
	[DllImport("user32.dll")] public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
	[DllImport("user32.dll")] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
	[DllImport("user32.dll")] public static extern bool ShowWindow(nint hwnd, int cmd);
	[DllImport("user32.dll")] public static extern bool ScreenToClient(nint hwnd, ref POINT p);
	[DllImport("user32.dll")] public static extern bool GetClientRect(nint hwnd, out RECT r);
	[DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
	[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(nint value);
	[DllImport("user32.dll")] public static extern bool PrintWindow(nint hwnd, nint dc, uint flags);
	[DllImport("user32.dll")] public static extern nint GetDC(nint hwnd);
	[DllImport("user32.dll")] public static extern int ReleaseDC(nint hwnd, nint dc);
	[DllImport("gdi32.dll")] public static extern nint CreateCompatibleDC(nint dc);
	[DllImport("gdi32.dll")] public static extern bool DeleteDC(nint dc);
	[DllImport("gdi32.dll")] public static extern nint CreateDIBSection(nint dc, ref BITMAPINFOHEADER header, uint usage, out nint bits, nint section, uint offset);
	[DllImport("gdi32.dll")] public static extern nint SelectObject(nint dc, nint obj);
	[DllImport("gdi32.dll")] public static extern bool DeleteObject(nint obj);
	[DllImport("ole32.dll")] public static extern int OleInitialize(nint reserved);
	[DllImport("ole32.dll")] private static extern int RegisterDragDrop(nint hwnd, nint target);
	[DllImport("ole32.dll")] public static extern int RevokeDragDrop(nint hwnd);
	[DllImport("CoreMessaging.dll")] public static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint controller);
	[DllImport("UIAutomationCore.dll")] public static extern nint UiaReturnRawElementProvider(nint hwnd, nint wParam, nint lParam, nint provider);
	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string? name);
}
