using System.Security.Cryptography;
using Zing.Core.Mcp;

namespace Zing.Core.Tests.Mcp;

public sealed class AiTokenTests : IDisposable
{
	private readonly string _dir = Directory.CreateTempSubdirectory("zing-aitoken-").FullName;

	public void Dispose() => Directory.Delete(_dir, recursive: true);

	private string TokenPath => Path.Combine(_dir, "ai-token.bin");

	[Fact]
	public void TokenSurvivesReload()
	{
		var first = new AiToken(new FakeCipher(), TokenPath).Current();
		Assert.Equal(32, first.Length);
		Assert.Equal(first, new AiToken(new FakeCipher(), TokenPath).Current());
	}

	[Fact]
	public void TokenIsNeverStoredInThePlain()
	{
		var token = new AiToken(new FakeCipher(), TokenPath).Current();
		Assert.True(File.ReadAllBytes(TokenPath).AsSpan().IndexOf(token) < 0);
	}

	[Fact]
	public void RegenerateChangesTokenAndRaises()
	{
		var store = new AiToken(new FakeCipher(), TokenPath);
		var before = store.Current();
		var raised = 0;
		store.Regenerated += () => raised++;

		var after = store.Regenerate();

		Assert.NotEqual(before, after);
		Assert.Equal(after, store.Current());
		Assert.Equal(after, new AiToken(new FakeCipher(), TokenPath).Current());
		Assert.Equal(1, raised);
	}

	[Fact]
	public void ATokenSealedForAnotherUserIsAnError()
	{
		new AiToken(new FakeCipher(), TokenPath).Current();
		Assert.ThrowsAny<CryptographicException>(() => new AiToken(new FakeCipher(foreign: true), TokenPath).Current());
	}
}
