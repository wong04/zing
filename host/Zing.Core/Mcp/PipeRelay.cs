using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zing.Core.Mcp;

/// <summary>What <c>zing-mcp.exe</c> does: copies newline-delimited JSON-RPC between an MCP client's stdio and Zing's pipe.</summary>
public static class PipeRelay
{
	public const string NotRunningMessage = "Zing isn't running or AI control is off";

	/// <summary>Thrown by the connect function when there is no pipe to relay to; its message is for the person.</summary>
	public sealed class PipeUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

	/// <summary>
	/// 1 when <paramref name="connect"/> threw <see cref="PipeUnavailableException"/> (the client gets a JSON-RPC error for its first
	/// request, and <paramref name="stderr"/> the message), when a line was past the pipe's size limit (the client gets
	/// <see cref="McpText.TooLong"/>), or when the pipe broke or Zing closed it; 0 when the client closed <paramref name="input"/>.
	/// </summary>
	public static async Task<int> Run(Stream input, Stream output, Func<Stream> connect, TextWriter stderr, CancellationToken cancel)
	{
		Stream pipe;
		try
		{
			pipe = connect();
		}
		catch (PipeUnavailableException unavailable)
		{
			await stderr.WriteLineAsync(unavailable.Message).ConfigureAwait(false);
			var id = await FirstRequestId(input, cancel).ConfigureAwait(false);
			await WriteLine(output, McpText.Error(id, -32000, unavailable.Message), cancel).ConfigureAwait(false);
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
			try
			{
				await first.ConfigureAwait(false);
				return first == toZing ? 0 : 1;
			}
			catch (InvalidDataException)
			{
				await WriteLine(output, McpText.TooLong, CancellationToken.None).ConfigureAwait(false);
				return 1;
			}
			catch (Exception broken) when (broken is IOException or ObjectDisposedException)
			{
				await stderr.WriteLineAsync($"The connection to Zing broke: {broken.Message}").ConfigureAwait(false);
				return 1;
			}
		}
	}

	private static async Task<JsonNode?> FirstRequestId(Stream input, CancellationToken cancel)
	{
		var line = await new LineReader(input, McpHttpServer.MaxBody).Next(cancel).ConfigureAwait(false);
		try
		{
			return line is null ? null : JsonNode.Parse(line)?["id"]?.DeepClone();
		}
		catch (JsonException)
		{
			return null;
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
