using System.Text;

namespace Zing.Core.Mcp;

/// <summary>What <c>zing-mcp.exe</c> does: copies newline-delimited JSON-RPC between an MCP client's stdio and Zing's pipe.</summary>
public static class PipeRelay
{
	public const string NotRunningMessage = "Zing isn't running or AI control is off";

	/// <summary>The one line a client gets when there is no pipe to relay to.</summary>
	public static readonly string NotRunning = McpText.Error(null, -32000, NotRunningMessage);

	/// <summary>
	/// 1 when <paramref name="connect"/> found no pipe (the error was written to <paramref name="output"/>) or Zing closed it first;
	/// 0 when the client closed <paramref name="input"/>. A line past the pipe's own limit throws, since Zing would refuse it anyway.
	/// </summary>
	public static async Task<int> Run(Stream input, Stream output, Func<Stream?> connect, CancellationToken cancel)
	{
		var pipe = connect();
		if (pipe is null)
		{
			await WriteLine(output, NotRunning, cancel).ConfigureAwait(false);
			return 1;
		}
		await using (pipe.ConfigureAwait(false))
		{
			using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
			var toZing = Copy(input, pipe, stop.Token);
			var fromZing = Copy(pipe, output, stop.Token);
			var first = await Task.WhenAny(toZing, fromZing).ConfigureAwait(false);
			await stop.CancelAsync().ConfigureAwait(false);
			// The loser may be blocked in a read that can't be cancelled (stdin), so it is left to end with the process.
			var other = first == toZing ? fromZing : toZing;
			_ = other.ContinueWith(static t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
			await first.ConfigureAwait(false);
			return first == toZing ? 0 : 1;
		}
	}

	private static async Task Copy(Stream from, Stream to, CancellationToken cancel)
	{
		var lines = new LineReader(from, McpHttpServer.MaxBody);
		while (await lines.Next(cancel).ConfigureAwait(false) is { } line)
		{
			if (line.Length == 0) continue;
			await WriteLine(to, line, cancel).ConfigureAwait(false);
		}
		if (lines.TooLong) throw new InvalidDataException("A message was longer than the MCP size limit.");
	}

	private static async Task WriteLine(Stream to, string line, CancellationToken cancel)
	{
		await to.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), cancel).ConfigureAwait(false);
		await to.FlushAsync(cancel).ConfigureAwait(false);
	}
}
