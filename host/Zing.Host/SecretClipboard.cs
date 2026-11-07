using System.ComponentModel;
using System.Runtime.InteropServices;
using Zing.Core.Vault;

namespace Zing.Host;

/// <summary>
/// The clipboard a copied password goes to. Besides the text it sets the three formats password managers set, so Windows keeps
/// it out of clipboard history (Win+V) and the cloud clipboard. The sequence number lets the caller clear it later only if nothing
/// else was copied since. In an e2e run the real clipboard is never opened: the text is kept in <see cref="Held"/> (TestHooks "read-secret-clipboard").
/// </summary>
internal sealed class SecretClipboard(nint owner) : ISecretClipboard
{
	private const uint CF_UNICODETEXT = 13;
	private const uint GMEM_MOVEABLE = 0x2;
	private const int Attempts = 10;
	private static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(30);

	/// <summary>The formats set with the text. The first is present with any content; the other two are a DWORD 0.</summary>
	public static readonly string[] ExclusionFormats = ["ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard"];

	private static long _fakeSequence;

	/// <summary>What an e2e run "put on the clipboard" last; null until it does, and after a clear.</summary>
	public static string? Held { get; private set; }

	public long SequenceNumber => TestHooks.IsEndToEndRun ? Volatile.Read(ref _fakeSequence) : GetClipboardSequenceNumber();

	public long Write(string text)
	{
		if (TestHooks.IsEndToEndRun)
		{
			Held = text;
			return Interlocked.Increment(ref _fakeSequence);
		}
		Open();
		try
		{
			if (!EmptyClipboard()) throw Failed("clear the clipboard");
			Put(CF_UNICODETEXT, System.Text.Encoding.Unicode.GetBytes(text + "\0"));
			Put(RegisterClipboardFormatW(ExclusionFormats[0]), BitConverter.GetBytes(1));
			Put(RegisterClipboardFormatW(ExclusionFormats[1]), BitConverter.GetBytes(0));
			Put(RegisterClipboardFormatW(ExclusionFormats[2]), BitConverter.GetBytes(0));
		}
		finally
		{
			CloseClipboard();
		}
		// After the close: the number moves with each change made while it was open.
		return GetClipboardSequenceNumber();
	}

	public void Clear()
	{
		if (TestHooks.IsEndToEndRun)
		{
			Held = null;
			Interlocked.Increment(ref _fakeSequence);
			return;
		}
		Open();
		try
		{
			if (!EmptyClipboard()) throw Failed("clear the clipboard");
		}
		finally
		{
			CloseClipboard();
		}
	}

	private void Open()
	{
		for (var attempt = 1; attempt <= Attempts; attempt++)
		{
			if (OpenClipboard(owner)) return;
			Thread.Sleep(Wait);
		}
		throw new FileClipboardException("Another program is using the clipboard, so the password wasn't copied. Try again in a moment.", new Win32Exception(Marshal.GetLastWin32Error()));
	}

	private static void Put(uint format, byte[] data)
	{
		var memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)data.Length);
		if (memory == 0) throw Failed("allocate memory for the password");
		var pointer = GlobalLock(memory);
		if (pointer == 0)
		{
			GlobalFree(memory);
			throw Failed("lock the memory for the password");
		}
		Marshal.Copy(data, 0, pointer, data.Length);
		GlobalUnlock(memory);
		if (SetClipboardData(format, memory) != 0) return;
		var error = Failed("put the password on the clipboard");
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

	[DllImport("user32.dll")]
	private static extern uint GetClipboardSequenceNumber();

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
