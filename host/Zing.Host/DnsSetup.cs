using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Zing.Core.Net;

namespace Zing.Host;

/// <summary>
/// Reads this PC's DNS and runs the Network &amp; VPN scripts. Reading is Windows PowerShell as the person; changing is the same
/// PowerShell started with <c>runas</c>, which is the UAC prompt. A test run never gets this class (see <see cref="TestHooks.DnsSystem"/>).
/// </summary>
public sealed class DnsSetup : IDnsSystem
{
	/// <summary>ERROR_CANCELLED: the person pressed No on the UAC prompt.</summary>
	private const int PromptRefused = 1223;

	private static readonly TimeSpan ListingDeadline = TimeSpan.FromSeconds(30);

	// By full path: an elevated process must not search a PATH the person's other programs can write to.
	private static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

	public async Task<DnsSnapshot> ReadAsync(CancellationToken ct)
	{
		// Redirected output uses the OEM code page unless told otherwise, which would garble an adapter called "Ethernät".
		var script = "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false\n" + DnsScript.List;
		var start = new ProcessStartInfo(PowerShell)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", DnsScript.EncodedCommand(script) }) start.ArgumentList.Add(argument);
		using var process = Process.Start(start) ?? throw new DnsListingError("PowerShell didn't start");
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
		deadline.CancelAfter(ListingDeadline);
		var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
		var error = process.StandardError.ReadToEndAsync(deadline.Token);
		try
		{
			await process.WaitForExitAsync(deadline.Token);
			var text = await output;
			if (process.ExitCode != 0) throw new DnsListingError($"PowerShell exited with code {process.ExitCode}: {(await error).Trim()}");
			return DnsSnapshot.Parse(text);
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested)
		{
			process.Kill(entireProcessTree: true);
			throw new DnsListingError("it took longer than 30 seconds");
		}
	}

	public async Task<ElevatedRun> RunElevatedAsync(string script, CancellationToken ct)
	{
		var start = new ProcessStartInfo(PowerShell, $"-NoProfile -NonInteractive -EncodedCommand {DnsScript.EncodedCommand(script)}")
		{
			UseShellExecute = true,
			Verb = "runas",
			WindowStyle = ProcessWindowStyle.Hidden,
		};
		Process? process;
		try
		{
			process = Process.Start(start);
		}
		catch (Win32Exception error) when (error.NativeErrorCode == PromptRefused)
		{
			return ElevatedRun.Declined;
		}
		using (process ?? throw new InvalidOperationException("Windows didn't start the elevated PowerShell"))
		{
			await process.WaitForExitAsync(ct);
			return new ElevatedRun.Exited(process.ExitCode);
		}
	}
}
