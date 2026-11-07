using System.ComponentModel;
using System.Runtime.InteropServices;
using Zing.Core.Vault;

namespace Zing.Host;

/// <summary>
/// The clipboard a copied password goes to: the Win32 clipboard behind <see cref="SecretClipboardWriter"/>, which sets the text with
/// the formats that keep it out of clipboard history and the cloud clipboard. The sequence number lets the caller clear it later only
/// if nothing else was copied since. In an e2e run the real clipboard is never opened: the text is kept in <see cref="Held"/>
/// (TestHooks "read-secret-clipboard").
/// </summary>
internal sealed class SecretClipboard(nint owner) : ISecretClipboard, IClipboardPort
{
	private const uint CF_UNICODETEXT = 13;
	private const uint GMEM_MOVEABLE = 0x2;
	private const int Attempts = 10;
	private static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(30);

	private static long _fakeSequence;

	/// <summary>What an e2e run "put on the clipboard" last; null until it does, and after a clear.</summary>
	public static string? Held { get; private set; }

	long IClipboardPort.SequenceNumber => GetClipboardSequenceNumber();

	public long SequenceNumber => TestHooks.IsEndToEndRun ? Volatile.Read(ref _fakeSequence) : GetClipboardSequenceNumber();

	public long Write(string text)
	{
		if (TestHooks.IsEndToEndRun)
		{
			Held = text;
			return Interlocked.Increment(ref _fakeSequence);
		}
		return SecretClipboardWriter.Write(this, text);
	}

	public void Clear()
	{
		if (TestHooks.IsEndToEndRun)
		{
			Held = null;
			Interlocked.Increment(ref _fakeSequence);
			return;
		}
		SecretClipboardWriter.Clear(this);
	}

	void IClipboardPort.Open()
	{
		for (var attempt = 1; attempt <= Attempts; attempt++)
		{
			if (OpenClipboard(owner)) return;
			Thread.Sleep(Wait);
		}
		throw new ClipboardBusyException("Another program is using the clipboard, so the password couldn't be copied or cleared. Try again in a moment.", new Win32Exception(Marshal.GetLastWin32Error()));
	}

	void IClipboardPort.Empty()
	{
		if (!EmptyClipboard()) throw Failed("clear the clipboard");
	}

	void IClipboardPort.PutText(byte[] utf16) => Put(CF_UNICODETEXT, utf16);

	void IClipboardPort.PutFlag(string format, int value) => Put(RegisterClipboardFormatW(format), BitConverter.GetBytes(value));

	void IClipboardPort.Close() => CloseClipboard();

	private static void Put(uint format, byte[] data)
	{
		var memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)data.Length);
		if (memory == 0) throw Failed("allocate memory for the password");
		var pointer = GlobalLock(memory);
		if (pointer == 0)
		{
			// The error is read before GlobalFree can replace it.
			var lockError = Failed("lock the memory for the password");
			GlobalFree(memory);
			throw lockError;
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
