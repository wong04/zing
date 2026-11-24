using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Zing.Core.Mcp;

namespace Zing.Core.Tests.Mcp;

public sealed class PipeRelayTests
{
	/// <summary>One end of an in-memory duplex connection.</summary>
	private sealed class Duplex(Stream read, Stream write) : Stream
	{
		public override bool CanRead => true;
		public override bool CanWrite => true;
		public override bool CanSeek => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override void Flush() => write.Flush();
		public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default) => read.ReadAsync(buffer, cancel);
		public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
		public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancel = default) => write.WriteAsync(buffer, cancel);
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				read.Dispose();
				write.Dispose();
			}
			base.Dispose(disposing);
		}
	}

	private static (Duplex Near, Duplex Far) Connection()
	{
		var toFar = new AnonymousPipeServerStream(PipeDirection.Out);
		var toNear = new AnonymousPipeServerStream(PipeDirection.Out);
		var farRead = new AnonymousPipeClientStream(PipeDirection.In, toFar.ClientSafePipeHandle);
		var nearRead = new AnonymousPipeClientStream(PipeDirection.In, toNear.ClientSafePipeHandle);
		return (new Duplex(nearRead, toFar), new Duplex(farRead, toNear));
	}

	private static async Task<string?> ReadLine(Stream stream) => await new LineReader(stream, 1 << 20).Next(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

	private static async Task Send(Stream stream, string line)
	{
		await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
		await stream.FlushAsync();
	}

	[Fact]
	public async Task RelayRoundTrips()
	{
		var (clientIn, clientSide) = Connection();
		var (clientOut, relayOut) = Connection();
		var (zingEnd, relayPipe) = Connection();
		using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var relay = PipeRelay.Run(clientSide, relayOut, () => relayPipe, TextWriter.Null, cancel.Token);

		await Send(clientIn, """{"jsonrpc":"2.0","id":1,"method":"ping"}""");
		Assert.Equal("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", await ReadLine(zingEnd));
		await Send(zingEnd, """{"jsonrpc":"2.0","id":1,"result":{}}""");
		Assert.Equal("""{"jsonrpc":"2.0","id":1,"result":{}}""", await ReadLine(clientOut));

		clientIn.Dispose();
		Assert.Equal(0, await relay);
	}

	private static MemoryStream Lines(string text) => new(Encoding.UTF8.GetBytes(text));

	[Fact]
	public async Task NoPipeWritesErrorAndExits1()
	{
		var (clientOut, relayOut) = Connection();
		var stderr = new StringWriter();
		var code = await PipeRelay.Run(Lines("""{"jsonrpc":"2.0","id":7,"method":"initialize"}""" + "\n"), relayOut,
			() => throw new PipeRelay.PipeUnavailableException(PipeRelay.NotRunningMessage), stderr, CancellationToken.None);

		Assert.Equal(1, code);
		var error = JsonNode.Parse((await ReadLine(clientOut))!)!;
		Assert.Equal(7, error["id"]!.GetValue<int>());
		Assert.Equal("Zing isn't running or AI control is off", error["error"]!["message"]!.GetValue<string>());
		Assert.Contains("Zing isn't running or AI control is off", stderr.ToString());
	}

	[Fact]
	public async Task NoPipeAnswersWithANullIdWhenNoRequestCame()
	{
		var (clientOut, relayOut) = Connection();
		var code = await PipeRelay.Run(new MemoryStream(), relayOut, () => throw new PipeRelay.PipeUnavailableException("nope"), TextWriter.Null, CancellationToken.None);

		Assert.Equal(1, code);
		Assert.Null(JsonNode.Parse((await ReadLine(clientOut))!)!["id"]);
	}

	[Fact]
	public async Task AnotherUsersPipeGivesItsOwnErrorAndExits1()
	{
		var (clientOut, relayOut) = Connection();
		var stderr = new StringWriter();
		var code = await PipeRelay.Run(Lines("""{"jsonrpc":"2.0","id":"a","method":"ping"}""" + "\n"), relayOut,
			() => throw new PipeRelay.PipeUnavailableException("Zing's pipe belongs to another Windows user", new UnauthorizedAccessException()), stderr, CancellationToken.None);

		Assert.Equal(1, code);
		var error = JsonNode.Parse((await ReadLine(clientOut))!)!;
		Assert.Equal("a", error["id"]!.GetValue<string>());
		Assert.Equal("Zing's pipe belongs to another Windows user", error["error"]!["message"]!.GetValue<string>());
		Assert.Contains("another Windows user", stderr.ToString());
	}

	[Fact]
	public async Task AnOversizedLineGetsTooLongAndExits1()
	{
		var (clientOut, relayOut) = Connection();
		var (_, relayPipe) = Connection();
		var code = await PipeRelay.Run(new MemoryStream(new byte[McpHttpServer.MaxBody + 10].Select(_ => (byte)'x').ToArray()), relayOut, () => relayPipe,
			TextWriter.Null, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

		Assert.Equal(1, code);
		Assert.Equal(McpText.TooLong, await ReadLine(clientOut));
	}

	[Fact]
	public async Task ZingClosingThePipeEndsTheRelayWith1()
	{
		var (clientIn, clientSide) = Connection();
		var (_, relayOut) = Connection();
		var (zingEnd, relayPipe) = Connection();
		var relay = PipeRelay.Run(clientSide, relayOut, () => relayPipe, TextWriter.Null, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

		zingEnd.Dispose();
		Assert.Equal(1, await relay);
		clientIn.Dispose();
	}
}
