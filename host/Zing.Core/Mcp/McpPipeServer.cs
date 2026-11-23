using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Zing.Core.Mcp;

/// <summary>
/// The pipe <c>zing-mcp.exe</c> relays stdio to: newline-delimited JSON-RPC, one message per line each way. Only the current
/// Windows user can open it, which is why it needs no token. Served on the pool's threads; <paramref name="handle"/> takes each
/// message to the UI thread. <paramref name="report"/> gets bugs, <paramref name="warn"/> clients that went away.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class McpPipeServer(string name, Func<string, Task<string?>> handle, Action<Exception> report, Action<Exception> warn) : IMcpListener
{
	private readonly CancellationTokenSource _stop = new();
	private readonly List<NamedPipeServerStream> _open = [];

	/// <summary>Built the way ProfileOwner names its pipe: a pipe's name is machine-wide, so it says which session it serves.</summary>
	public static string Name(string profileKey) => $"zing-mcp-{profileKey}-{Process.GetCurrentProcess().SessionId}";

	/// <summary>
	/// Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the name is already taken, by another
	/// Zing or by something squatting on it to read what clients send.
	/// </summary>
	public void Start()
	{
		var first = Create(PipeOptions.FirstPipeInstance);
		_ = Task.Run(() => Accept(first));
	}

	public void Stop()
	{
		_stop.Cancel();
		lock (_open)
		{
			foreach (var pipe in _open) pipe.Dispose();
			_open.Clear();
		}
	}

	public static PipeSecurity UserOnly()
	{
		var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The current Windows identity has no user SID.");
		var security = new PipeSecurity();
		security.SetOwner(user);
		security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
		security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
		return security;
	}

	// CurrentUserOnly can't be combined with an explicit ACL (.NET throws), and the explicit one is the stricter: it is
	// protected, so nothing is inherited. Clients still pass CurrentUserOnly, which checks this pipe's owner is their user.
	private NamedPipeServerStream Create(PipeOptions extra)
	{
		var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
			PipeOptions.Asynchronous | extra, 0, 0, UserOnly());
		lock (_open) _open.Add(pipe);
		return pipe;
	}

	private async Task Accept(NamedPipeServerStream listening)
	{
		while (true)
		{
			try
			{
				await listening.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
			}
			catch (Exception) when (_stop.IsCancellationRequested)
			{
				return;
			}
			catch (Exception error)
			{
				report(error);
				return;
			}
			var connected = listening;
			_ = Task.Run(() => Serve(connected));
			try
			{
				listening = Create(PipeOptions.None);
			}
			catch (Exception) when (_stop.IsCancellationRequested)
			{
				return;
			}
			catch (Exception error)
			{
				report(error);
				return;
			}
		}
	}

	private async Task Serve(NamedPipeServerStream pipe)
	{
		try
		{
			using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
			var lines = new LineReader(pipe, McpHttpServer.MaxBody);
			while (await lines.Next(_stop.Token).ConfigureAwait(false) is { } line)
			{
				if (line.Length == 0) continue;
				var reply = await handle(line).ConfigureAwait(false);
				if (reply is null) continue;
				await writer.WriteLineAsync(reply.AsMemory(), _stop.Token).ConfigureAwait(false);
				await writer.FlushAsync(_stop.Token).ConfigureAwait(false);
			}
			if (lines.TooLong)
			{
				await writer.WriteLineAsync(McpText.TooLong.AsMemory(), _stop.Token).ConfigureAwait(false);
				await writer.FlushAsync(_stop.Token).ConfigureAwait(false);
			}
		}
		catch (Exception) when (_stop.IsCancellationRequested)
		{
			// Stopped: the pipe was closed under the read on purpose.
		}
		catch (Exception error) when (error is IOException or ObjectDisposedException)
		{
			warn(error);
		}
		catch (Exception error)
		{
			report(error);
		}
		finally
		{
			lock (_open) _open.Remove(pipe);
			pipe.Dispose();
		}
	}
}
