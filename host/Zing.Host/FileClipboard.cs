using System.ComponentModel;
using System.Runtime.InteropServices;
using Zing.Core;

namespace Zing.Host;

/// <summary>The clipboard is held by another program, or refused the data.</summary>
public sealed class FileClipboardException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>
/// Puts files on the system clipboard as CF_HDROP (what Explorer pastes) with a copy drop effect. The clipboard belongs to the
/// thread that opens it, and the window that owns the data must be that thread's, so this runs on the UI thread with the
/// window's handle. In an e2e run the real clipboard is never opened: the block is kept in <see cref="Held"/> for the test
/// hook to read back (TestHooks "read-clipboard").
/// </summary>
internal static class FileClipboard
{
	private const uint CF_HDROP = 15;
	private const uint GMEM_MOVEABLE = 0x2;
	private const uint DROPEFFECT_COPY = 1;
	private const int Attempts = 10;
	private static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(30);

	/// <summary>What an e2e run "put on the clipboard" last; null until it does.</summary>
	public static byte[]? Held { get; private set; }

	public static void Copy(nint owner, IReadOnlyList<string> paths)
	{
		var block = DropFiles.Build(paths);
		if (TestHooks.IsEndToEndRun)
		{
			Held = block;
			return;
		}
		Write(owner, block);
	}

	private static void Write(nint owner, byte[] block)
	{
		Open(owner);
		try
		{
			if (!EmptyClipboard()) throw Failed("clear the clipboard");
			Put(CF_HDROP, block);
			Put(RegisterClipboardFormatW("Preferred DropEffect"), BitConverter.GetBytes(DROPEFFECT_COPY));
		}
		finally
		{
			CloseClipboard();
		}
	}

	private static void Open(nint owner)
	{
		for (var attempt = 1; attempt <= Attempts; attempt++)
		{
			if (OpenClipboard(owner)) return;
			Thread.Sleep(Wait);
		}
		throw new FileClipboardException("Another program is using the clipboard, so the file wasn't copied. Try again in a moment.", new Win32Exception(Marshal.GetLastWin32Error()));
	}

	private static void Put(uint format, byte[] data)
	{
		var memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)data.Length);
		if (memory == 0) throw Failed("allocate memory for the file");
		var pointer = GlobalLock(memory);
		if (pointer == 0)
		{
			GlobalFree(memory);
			throw Failed("lock the memory for the file");
		}
		Marshal.Copy(data, 0, pointer, data.Length);
		GlobalUnlock(memory);
		// On success the clipboard owns the memory; on failure it is still ours to free.
		if (SetClipboardData(format, memory) != 0) return;
		var error = Failed("put the file on the clipboard");
		GlobalFree(memory);
		throw error;
	}

	private static FileClipboardException Failed(string what) => new($"Windows would not {what}.", new Win32Exception(Marshal.GetLastWin32Error()));

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool OpenClipboard(nint owner);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool EmptyClipboard();

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseClipboard();

	[DllImport("user32.dll", SetLastError = true)]
	private static extern nint SetClipboardData(uint format, nint memory);

	[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern uint RegisterClipboardFormatW(string name);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern nint GlobalAlloc(uint flags, nuint bytes);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern nint GlobalLock(nint memory);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GlobalUnlock(nint memory);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern nint GlobalFree(nint memory);
}
