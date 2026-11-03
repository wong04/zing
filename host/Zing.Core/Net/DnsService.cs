using System.Security.Cryptography;
using System.Text;

namespace Zing.Core.Net;

/// <param name="Applied">Encrypted DNS was set up by Zing and its previous settings are saved, so the script shown is Undo's.</param>
/// <param name="Token">Names the script shown (a hash of it). Apply and Undo take it back, and run only that script.</param>
public sealed record DnsPreview(bool Applied, string Provider, string ProviderName, IReadOnlyList<string> Adapters, string Script, string Token, string BackupPath, DateTimeOffset? SavedAt);

/// <param name="Preview">With <see cref="Changed"/>: the script that is there now, which nothing has run.</param>
public sealed record DnsOutcome(string Status, DnsPreview? Preview)
{
	public const string Applied = "applied";
	public const string Undone = "undone";
	public const string Declined = "declined";
	public const string Changed = "changed";
}

/// <summary>
/// Encrypted DNS for the whole PC: shows the script, runs it after a click, and keeps the previous settings until Undo.
/// The renderer never supplies script text; it names the script it was shown by a token and the host builds it again.
/// </summary>
public sealed class DnsService(IDnsSystem system, string backupPath, Func<DateTimeOffset> now)
{
	// One change at a time, so a double click can't run a script twice or save a backup of already-changed settings.
	private readonly SemaphoreSlim _gate = new(1, 1);

	public async Task<DnsPreview> PreviewAsync(string provider)
	{
		DnsProviders.Get(provider);
		await _gate.WaitAsync();
		try
		{
			if (!DnsBackup.Exists(backupPath)) return ApplyPreview(provider, await system.ReadAsync(CancellationToken.None));
			return UndoPreview(DnsBackup.Load(backupPath));
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task<DnsOutcome> ApplyAsync(string provider, string token)
	{
		var chosen = DnsProviders.Get(provider);
		await _gate.WaitAsync();
		try
		{
			if (DnsBackup.Exists(backupPath))
			{
				// A corrupt file is reported as that; a good one means the originals are already saved and must not be overwritten.
				DnsBackup.Load(backupPath);
				throw new DnsAlreadyAppliedError();
			}
			var snapshot = await system.ReadAsync(CancellationToken.None);
			var script = DnsScript.Apply(provider, snapshot.Adapters);
			if (TokenOf(script) != token) return new DnsOutcome(DnsOutcome.Changed, ApplyPreview(provider, snapshot));
			// Saved before the script runs: a script that stops half way has changed some adapters, and Undo needs the originals.
			var owned = chosen.Servers.ToHashSet();
			DnsBackup.Save(backupPath, new DnsBackupFile(provider, now(), snapshot.Adapters, [.. snapshot.Doh.Where(entry => owned.Contains(entry.Address))]));
			var run = await system.RunElevatedAsync(script, CancellationToken.None);
			switch (run)
			{
				case ElevatedRun.Exited { Code: 0 }:
					return new DnsOutcome(DnsOutcome.Applied, null);
				case ElevatedRun.Exited { Code: DnsScript.NoEncryptedDnsExit }:
					DnsBackup.Delete(backupPath);
					throw new NoEncryptedDnsError();
				case ElevatedRun.Exited failed:
					throw new DnsScriptFailedError(failed.Code, undoKept: true);
				default:
					DnsBackup.Delete(backupPath);
					return new DnsOutcome(DnsOutcome.Declined, null);
			}
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task<DnsOutcome> UndoAsync(string token)
	{
		await _gate.WaitAsync();
		try
		{
			var saved = DnsBackup.Load(backupPath);
			var preview = UndoPreview(saved);
			if (preview.Token != token) return new DnsOutcome(DnsOutcome.Changed, preview);
			var run = await system.RunElevatedAsync(preview.Script, CancellationToken.None);
			switch (run)
			{
				case ElevatedRun.Exited { Code: 0 }:
					DnsBackup.Delete(backupPath);
					return new DnsOutcome(DnsOutcome.Undone, null);
				case ElevatedRun.Exited failed:
					throw new DnsScriptFailedError(failed.Code, undoKept: true);
				default:
					return new DnsOutcome(DnsOutcome.Declined, null);
			}
		}
		finally
		{
			_gate.Release();
		}
	}

	private DnsPreview ApplyPreview(string provider, DnsSnapshot snapshot)
	{
		var script = DnsScript.Apply(provider, snapshot.Adapters);
		return new DnsPreview(false, provider, DnsProviders.Get(provider).Name, [.. snapshot.Adapters.Select(a => a.Name)], script, TokenOf(script), backupPath, null);
	}

	private DnsPreview UndoPreview(DnsBackupFile saved)
	{
		var script = DnsScript.Undo(saved.Adapters, saved.Doh, saved.Provider);
		return new DnsPreview(true, saved.Provider, DnsProviders.Get(saved.Provider).Name, [.. saved.Adapters.Select(a => a.Name)], script, TokenOf(script), backupPath, saved.SavedAt);
	}

	private static string TokenOf(string script) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script))).ToLowerInvariant();
}
