using System.Security.Cryptography;
using Zing.Core.Mcp;

namespace Zing.Core.Tests.Mcp;

public sealed class AiRequestCheckTests
{
	private const int Port = 47821;

	private static readonly byte[] Token = RandomNumberGenerator.GetBytes(32);

	private static string Bearer => "Bearer " + AiRequestCheck.Encode(Token);

	[Fact]
	public void ForeignOriginIsForbidden()
	{
		Assert.Equal(AiRequestCheck.Result.Forbidden, AiRequestCheck.Check($"127.0.0.1:{Port}", "https://evil.example", Bearer, Port, Token));
		Assert.Equal(AiRequestCheck.Result.Forbidden, AiRequestCheck.Check($"127.0.0.1:{Port}", "null", Bearer, Port, Token));
		Assert.Equal(AiRequestCheck.Result.Forbidden, AiRequestCheck.Check($"127.0.0.1:{Port}", $"http://127.0.0.1:{Port + 1}", Bearer, Port, Token));
	}

	[Fact]
	public void RebindHostIsForbidden()
	{
		Assert.Equal(AiRequestCheck.Result.Forbidden, AiRequestCheck.Check($"evil.example:{Port}", null, Bearer, Port, Token));
		Assert.Equal(AiRequestCheck.Result.Forbidden, AiRequestCheck.Check("127.0.0.1", null, Bearer, Port, Token));
		Assert.Equal(AiRequestCheck.Result.Forbidden, AiRequestCheck.Check(null, null, Bearer, Port, Token));
		Assert.Equal(AiRequestCheck.Result.Forbidden, AiRequestCheck.Check($"localhost.evil.example:{Port}", null, Bearer, Port, Token));
	}

	[Fact]
	public void ForbiddenIsCheckedBeforeTheToken() =>
		Assert.Equal(AiRequestCheck.Result.Forbidden, AiRequestCheck.Check($"evil.example:{Port}", null, null, Port, Token));

	[Fact]
	public void MissingOriginIsAllowed()
	{
		Assert.Equal(AiRequestCheck.Result.Ok, AiRequestCheck.Check($"127.0.0.1:{Port}", null, Bearer, Port, Token));
		Assert.Equal(AiRequestCheck.Result.Ok, AiRequestCheck.Check($"LocalHost:{Port}", $"http://localhost:{Port}", Bearer, Port, Token));
	}

	[Fact]
	public void WrongTokenIsUnauthorized()
	{
		var other = AiRequestCheck.Encode(RandomNumberGenerator.GetBytes(32));
		Assert.Equal(AiRequestCheck.Result.Unauthorized, AiRequestCheck.Check($"127.0.0.1:{Port}", null, "Bearer " + other, Port, Token));
		Assert.Equal(AiRequestCheck.Result.Unauthorized, AiRequestCheck.Check($"127.0.0.1:{Port}", null, null, Port, Token));
		Assert.Equal(AiRequestCheck.Result.Unauthorized, AiRequestCheck.Check($"127.0.0.1:{Port}", null, "Bearer !!not-base64!!", Port, Token));
		Assert.Equal(AiRequestCheck.Result.Unauthorized, AiRequestCheck.Check($"127.0.0.1:{Port}", null, "Basic " + AiRequestCheck.Encode(Token), Port, Token));
		Assert.Equal(AiRequestCheck.Result.Unauthorized, AiRequestCheck.Check($"127.0.0.1:{Port}", null, "Bearer " + AiRequestCheck.Encode(Token[..16]), Port, Token));
	}

	[Fact]
	public void LoopbackWithTokenIsOk() =>
		Assert.Equal(AiRequestCheck.Result.Ok, AiRequestCheck.Check($"127.0.0.1:{Port}", $"http://127.0.0.1:{Port}", Bearer, Port, Token));
}
