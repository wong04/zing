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
			var pending = new Pending();
			var toZing = Copy(input, pipe, pending.Asked, stop.Token);
			var fromZing = Copy(pipe, output, pending.Answered, stop.Token);
			var first = await Task.WhenAny(toZing, fromZing).ConfigureAwait(false);
			// The client closed stdin with requests still out: their answers go to it before the relay ends, unless Zing closes first.
			if (first == toZing && toZing.IsCompletedSuccessfully && await Task.WhenAny(pending.InputClosed(), fromZing).ConfigureAwait(false) == fromZing)
				first = fromZing;
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

	private static async Task Copy(Stream from, Stream to, Action<string> seen, CancellationToken cancel)
	{
		var lines = new LineReader(from, McpHttpServer.MaxBody);
		while (await lines.Next(cancel).ConfigureAwait(false) is { } line)
		{
			if (line.Length == 0) continue;
			seen(line);
			await WriteLine(to, line, cancel).ConfigureAwait(false);
		}
		if (lines.TooLong) throw new InvalidDataException("A message was longer than the MCP size limit.");
	}

	private static async Task WriteLine(Stream to, string line, CancellationToken cancel)
	{
		await to.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), cancel).ConfigureAwait(false);
		await to.FlushAsync(cancel).ConfigureAwait(false);
	}

	/// <summary>The ids of requests sent to Zing and not yet answered; both copies report to it from their own threads.</summary>
	private sealed class Pending
	{
		private readonly Lock _gate = new();
		private readonly HashSet<string> _ids = [];
		private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private bool _inputClosed;

		public void Asked(string line)
		{
			if (Id(line, request: true) is not { } id) return;
			lock (_gate) _ids.Add(id);
		}

		public void Answered(string line)
		{
			if (Id(line, request: false) is not { } id) return;
			lock (_gate)
			{
				_ids.Remove(id);
				if (_inputClosed && _ids.Count == 0) _drained.TrySetResult();
			}
		}

		/// <summary>Completes once every request sent has its answer.</summary>
		public Task InputClosed()
		{
			lock (_gate)
			{
				_inputClosed = true;
				if (_ids.Count == 0) _drained.TrySetResult();
			}
			return _drained.Task;
		}

		/// <summary>A request's id (it has a method) or a response's (it has none); null for a notification or a line that isn't JSON.</summary>
		private static string? Id(string line, bool request)
		{
			try
			{
				var message = JsonNode.Parse(line) as JsonObject;
				if (message is null || message.ContainsKey("method") != request || message["id"] is not { } id) return null;
				return id.ToJsonString();
			}
			catch (JsonException)
			{
				// Zing answers a line that isn't JSON itself, with a null id, so it is never waited for.
				return null;
			}
		}
	}
}