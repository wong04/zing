using Zing.Core.Vault;

namespace Zing.Core.Tests.Vault;

/// <summary>What a password write does to the clipboard, over a fake port: the real clipboard is never touched.</summary>
public sealed class SecretClipboardWriterTests
{
	private sealed class FakePort : IClipboardPort
	{
		public List<string> Ops { get; } = [];
		public byte[]? Text { get; private set; }
		public int FailPutFlagNumber { get; set; }
		private int _flags;

		public long SequenceNumber => 7;

		public void Open() => Ops.Add("open");
		public void Empty() => Ops.Add("empty");
		public void Close() => Ops.Add("close");

		public void PutText(byte[] utf16)
		{
			Text = utf16;
			Ops.Add("text");
		}

		public void PutFlag(string format, int value)
		{
			if (++_flags == FailPutFlagNumber) throw new InvalidOperationException($"Windows would not set {format}");
			Ops.Add($"{format}={value}");
		}
	}

	[Fact]
	public void WritesTheTextWithTheThreeExclusionFormatsAndReturnsTheSequenceNumber()
	{
		var port = new FakePort();

		var sequence = SecretClipboardWriter.Write(port, "pw");

		Assert.Equal(7, sequence);
		Assert.Equal(["open", "empty", "text", "ExcludeClipboardContentFromMonitorProcessing=1", "CanIncludeInClipboardHistory=0", "CanUploadToCloudClipboard=0", "close"], port.Ops);
	}

	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	public void AFormatThatCantBeSetEmptiesTheClipboardAgainBeforeTheErrorGoesOn(int failing)
	{
		var port = new FakePort { FailPutFlagNumber = failing };

		Assert.Throws<InvalidOperationException>(() => SecretClipboardWriter.Write(port, "pw"));

		Assert.Equal(["empty", "close"], port.Ops[^2..]);
		Assert.Equal(2, port.Ops.Count(op => op == "empty"));
	}

	[Fact]
	public void ThePlaintextBytesAreZeroedAfterTheWrite()
	{
		var port = new FakePort();

		SecretClipboardWriter.Write(port, "pw");

		Assert.All(port.Text!, b => Assert.Equal(0, b));
	}

	[Fact]
	public void TheBytesAreZeroedWhenTheWriteFailsToo()
	{
		var port = new FakePort { FailPutFlagNumber = 1 };

		Assert.Throws<InvalidOperationException>(() => SecretClipboardWriter.Write(port, "pw"));

		Assert.All(port.Text!, b => Assert.Equal(0, b));
	}
}
