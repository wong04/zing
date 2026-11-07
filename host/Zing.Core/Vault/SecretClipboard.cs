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

/// <summary>
/// Copies a password and clears the clipboard 30 seconds later, but only if nothing else was copied meanwhile: the sequence number
/// is the proof, and it needs no permission to read the clipboard.
/// </summary>
public sealed class TimedSecretCopy(ISecretClipboard clipboard, IScheduler scheduler) : IDisposable
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
		if (clipboard.SequenceNumber == _written) clipboard.Clear();
	}
}
