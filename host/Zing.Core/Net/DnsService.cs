using System.Security.Cryptography;
using System.Text;

namespace Zing.Core.Net;

/// <param name="Applied">Encrypted DNS was set up by Zing and its previous settings are saved, so the script shown is Undo's.</param>
/// <param name="Adapters">Their names with hidden characters spelled out (<see cref="DnsDisplay"/>).</param>
/// <param name="Script">The exact text that runs. Not for display: it can hold characters that rearrange what is read.</param>
/// <param name="ScriptShown">The same script with hidden characters spelled out; this is what the page shows.</param>
/// <param name="Token">Names the script shown (a hash of <paramref name="Script"/>). Apply and Undo take it back, and run only that script.</param>
/// <param name="DohSupported">This Windows has the encrypted-DNS commands. Without them Apply is not offered.</param>
/// <param name="Changes">With <paramref name="Applied"/>: what Undo will set, one line each, built from the same checked data as the script.</param>
public sealed record DnsPreview(bool Applied, string Provider, string ProviderName, IReadOnlyList<string> Adapters, string Script, string ScriptShown, string Token, string BackupPath, DateTimeOffset? SavedAt, bool DohSupported, IReadOnlyList<string> Changes);

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
/// <param name="runLimit">How long an elevated run may take before it is given up on; two minutes by default.</param>
public sealed class DnsService(IDnsSystem system, string backupPath, Func<DateTimeOffset> now, TimeSpan? runLimit = null)
{
	// One change at a time, so a double click can't run a script twice or save a backup of already-changed settings.
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly TimeSpan _runLimit = runLimit ?? TimeSpan.FromMinutes(2);

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
			if (!snapshot.DohSupported) throw new NoEncryptedDnsError();
			var script = DnsScript.Apply(provider, snapshot.Adapters);
			if (TokenOf(script) != token) return new DnsOutcome(DnsOutcome.Changed, ApplyPreview(provider, snapshot));
			// Saved before the script runs: a script that stops half way has changed some adapters, and Undo needs the originals.
			var owned = chosen.Servers.ToHashSet();
			DnsBackup.Save(backupPath, new DnsBackupFile(provider, now(), snapshot.Adapters, [.. snapshot.Doh.Where(entry => owned.Contains(entry.Address))]));
			var run = await RunAsync(script);
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
			var run = await RunAsync(preview.Script);
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

	/// <summary>A run that hangs would hold the lock for good. The saved settings stay, as the script may have changed some.</summary>
	private async Task<ElevatedRun> RunAsync(string script)
	{
		using var limit = new CancellationTokenSource(_runLimit);
		try
		{
			return await system.RunElevatedAsync(script, limit.Token);
		}
		catch (OperationCanceledException) when (limit.IsCancellationRequested)
		{
			throw new DnsOutcomeUnknownError();
		}
	}

	private DnsPreview ApplyPreview(string provider, DnsSnapshot snapshot)
	{
		var script = DnsScript.Apply(provider, snapshot.Adapters);
		return new DnsPreview(false, provider, DnsProviders.Get(provider).Name, [.. snapshot.Adapters.Select(a => DnsDisplay.Visible(a.Name))], script, DnsDisplay.VisibleScript(script), TokenOf(script), backupPath, null, snapshot.DohSupported, []);
	}

	private DnsPreview UndoPreview(DnsBackupFile saved)
	{
		var script = DnsScript.Undo(saved.Adapters, saved.Doh, saved.Provider);
		return new DnsPreview(true, saved.Provider, DnsProviders.Get(saved.Provider).Name, [.. saved.Adapters.Select(a => DnsDisplay.Visible(a.Name))], script, DnsDisplay.VisibleScript(script), TokenOf(script), backupPath, saved.SavedAt, true, DnsScript.UndoSummary(saved.Adapters, saved.Doh, saved.Provider));
	}

	private static string TokenOf(string script) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script))).ToLowerInvariant();
}
