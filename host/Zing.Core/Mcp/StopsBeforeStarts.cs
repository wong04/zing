namespace Zing.Core.Mcp;

/// <summary>
/// Orders a key's start after its last stop: a stop sent late (an unshare's Fetch.disable) would otherwise land after the
/// next start (a reshare's Fetch.enable) and undo it. On the UI thread only; no locks.
/// </summary>
public sealed class StopsBeforeStarts<TKey> where TKey : notnull
{
	private readonly Dictionary<TKey, Task> _stopping = [];

	public void Stopping(TKey key, Task stop)
	{
		foreach (var done in _stopping.Where(s => s.Value.IsCompleted).Select(s => s.Key).ToList()) _stopping.Remove(done);
		_stopping[key] = stop;
	}

	/// <summary>Done once the key's last stop has ended, however it ended: a stop's failure is reported where it runs.</summary>
	public async Task StoppedAsync(TKey key)
	{
		while (_stopping.TryGetValue(key, out var stop))
		{
			await Task.WhenAny(stop);
			if (_stopping.TryGetValue(key, out var now) && now == stop) _stopping.Remove(key);
		}
	}
}
