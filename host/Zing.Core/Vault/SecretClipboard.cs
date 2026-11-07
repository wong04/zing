namespace Zing.Core.Vault;

/// <summary>
/// Where a copied password goes. The real one (Zing.Host.SecretClipboard) writes it with the formats that keep it out of
/// Windows' clipboard history and cloud clipboard; tests and e2e runs use one in memory.
/// </summary>
public interface ISecretClipboard
{
	/// <summary>Replaces the clipboard's contents with the text, kept out of clipboard history and cloud sync. Returns the sequence number after the write.</summary>
	long Write(string text);

	/// <summary>Windows' clipboard sequence number: it changes whenever anything is copied or cleared.</summary>
	long SequenceNumber { get; }

	void Clear();
}

/// <summary>The clipboard is held by another program, so a password could not be put on it or taken off it.</summary>
public sealed class ClipboardBusyException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>The few clipboard operations a password write needs, over the Win32 clipboard on the host and a fake in tests.</summary>
public interface IClipboardPort
{
	/// <exception cref="ClipboardBusyException">Another program holds the clipboard.</exception>
	void Open();
	void Empty();
	void PutText(byte[] utf16);
	/// <summary>A format that is only a flag: present with this DWORD value.</summary>
	void PutFlag(string format, int value);
	void Close();
	long SequenceNumber { get; }
}

public static class SecretClipboardWriter
{
	/// <summary>The formats set with the text: the first excludes it from monitoring, the other two (value 0) keep it out of history and the cloud clipboard.</summary>
	public static readonly string[] ExclusionFormats = ["ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard"];

	/// <summary>
	/// Replaces the clipboard with the text and the exclusion formats. If any format can't be set the clipboard is emptied again before
	/// the error goes on, so the password is never left on it without them. Returns the sequence number after the write.
	/// </summary>
	public static long Write(IClipboardPort port, string text)
	{
		var bytes = System.Text.Encoding.Unicode.GetBytes(text + "\0");
		try
		{
			port.Open();
			try
			{
				port.Empty();
				try
				{
					port.PutText(bytes);
					port.PutFlag(ExclusionFormats[0], 1);
					port.PutFlag(ExclusionFormats[1], 0);
					port.PutFlag(ExclusionFormats[2], 0);
				}
				catch
				{
					port.Empty();
					throw;
				}
			}
			finally
			{
				port.Close();
			}
		}
		finally
		{
			System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
		}
		// After the close: the number moves with each change made while it was open.
		return port.SequenceNumber;
	}

	public static void Clear(IClipboardPort port)
	{
		port.Open();
		try
		{
			port.Empty();
		}
		finally
		{
			port.Close();
		}
	}
}

/// <summary>
/// Copies a password and clears the clipboard 30 seconds later, but only if nothing else was copied meanwhile: the sequence number
/// is the proof, and it needs no permission to read the clipboard.
/// </summary>
/// <param name="clearFailed">Told when the clear failed because the clipboard was busy: there is no caller to throw to, and the password is still on it.</param>
public sealed class TimedSecretCopy(ISecretClipboard clipboard, IScheduler scheduler, Action<ClipboardBusyException> clearFailed) : IDisposable
{
	public static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(30);

	private IDisposable? _timer;
	private long _written;
	private bool _pending;

	public void Copy(string secret)
	{
		_timer?.Dispose();
		_written = clipboard.Write(secret);
		_pending = true;
		_timer = scheduler.Schedule(ClearAfter, ClearIfUnchanged);
	}

	/// <summary>The window is closing: its password comes off the clipboard now rather than staying there.</summary>
	public void Dispose()
	{
		_timer?.Dispose();
		_timer = null;
		ClearIfUnchanged();
	}

	private void ClearIfUnchanged()
	{
		if (!_pending) return;
		_pending = false;
		if (clipboard.SequenceNumber != _written) return;
		try
		{
			clipboard.Clear();
		}
		catch (ClipboardBusyException error)
		{
			clearFailed(error);
		}
	}
}
