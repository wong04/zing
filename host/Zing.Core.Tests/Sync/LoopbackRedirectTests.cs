using Zing.Core.Sync;

namespace Zing.Core.Tests.Sync;

/// <summary>What Google sign-in's loopback listener makes of each request: only a GET of exactly "/" with this sign-in's state counts.</summary>
public sealed class LoopbackRedirectTests
{
	private const string State = "state-1";

	private static LoopbackRequest Read(string target, string method = "GET") =>
		LoopbackRedirect.Read($"{method} {target} HTTP/1.1\r\nHost: 127.0.0.1:5000\r\n\r\n", State);

	[Fact]
	public void TheRedirectGivesItsCode() => Assert.Equal(new LoopbackRequest(LoopbackRequestKind.Code, "abc"), Read($"/?code=abc&state={State}"));

	[Fact]
	public void GooglesRefusalGivesItsError() => Assert.Equal(new LoopbackRequest(LoopbackRequestKind.Error, "access_denied"), Read($"/?error=access_denied&state={State}"));

	[Fact]
	public void AnotherPathIsNotTheRedirect() => Assert.Equal(LoopbackRequestKind.NotFound, Read($"/callback?code=abc&state={State}").Kind);

	[Fact]
	public void AFaviconIsNotTheRedirect() => Assert.Equal(LoopbackRequestKind.NotFound, Read("/favicon.ico").Kind);

	[Fact]
	public void TheRootWithoutACodeIsNotTheRedirect() => Assert.Equal(LoopbackRequestKind.NotFound, Read("/").Kind);

	[Fact]
	public void AnotherSignInsStateIsRefused() => Assert.Equal(LoopbackRequestKind.WrongState, Read("/?code=abc&state=other").Kind);

	[Fact]
	public void AMissingStateIsRefused() => Assert.Equal(LoopbackRequestKind.WrongState, Read("/?code=abc").Kind);

	[Fact]
	public void APostIsNotUnderstood() => Assert.Equal(LoopbackRequestKind.NotHttp, Read($"/?code=abc&state={State}", "POST").Kind);

	[Fact]
	public void AnIncompleteHeadIsNotUnderstood() => Assert.Equal(LoopbackRequestKind.NotHttp, LoopbackRedirect.Read("GET /?code=abc", State).Kind);

	[Fact]
	public void AnAbsoluteTargetIsNotUnderstood() => Assert.Equal(LoopbackRequestKind.NotHttp, Read($"http://evil.test/?code=abc&state={State}").Kind);
}
