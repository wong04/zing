using Zing.Core.Mcp;

namespace Zing.Core.Tests.Mcp;

public sealed class McpHttpSessionsTests
{
	private const string Initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";
	private const string ToolsList = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";

	[Fact]
	public void InitializeOpensASession()
	{
		var sessions = new McpHttpSessions();
		var admit = sessions.Admit("POST", null, Initialize);
		Assert.Equal(McpHttpSessions.Verdict.Handle, admit.Verdict);
		Assert.NotNull(admit.NewSession);
		Assert.Equal(McpHttpSessions.Verdict.Handle, sessions.Admit("POST", admit.NewSession, ToolsList).Verdict);
	}

	[Fact]
	public void SessionIdsAreUnguessable()
	{
		var sessions = new McpHttpSessions();
		var a = sessions.Admit("POST", null, Initialize).NewSession!;
		var b = sessions.Admit("POST", null, Initialize).NewSession!;
		Assert.NotEqual(a, b);
		Assert.True(a.Length >= 32);
	}

	[Fact]
	public void UnknownSessionIsNotFound() =>
		Assert.Equal(McpHttpSessions.Verdict.NotFound, new McpHttpSessions().Admit("POST", "made-up", ToolsList).Verdict);

	[Fact]
	public void MissingSessionIsABadRequest() =>
		Assert.Equal(McpHttpSessions.Verdict.BadRequest, new McpHttpSessions().Admit("POST", null, ToolsList).Verdict);

	[Fact]
	public void GetIsNotAllowed() =>
		Assert.Equal(McpHttpSessions.Verdict.MethodNotAllowed, new McpHttpSessions().Admit("GET", null, "").Verdict);

	[Fact]
	public void DeleteEndsTheSession()
	{
		var sessions = new McpHttpSessions();
		var id = sessions.Admit("POST", null, Initialize).NewSession!;
		Assert.Equal(McpHttpSessions.Verdict.Ended, sessions.Admit("DELETE", id, "").Verdict);
		Assert.Equal(McpHttpSessions.Verdict.NotFound, sessions.Admit("POST", id, ToolsList).Verdict);
		Assert.Equal(McpHttpSessions.Verdict.NotFound, sessions.Admit("DELETE", id, "").Verdict);
	}

	[Fact]
	public void DropAllEndsEverySession()
	{
		var sessions = new McpHttpSessions();
		var id = sessions.Admit("POST", null, Initialize).NewSession!;
		sessions.DropAll();
		Assert.Equal(McpHttpSessions.Verdict.NotFound, sessions.Admit("POST", id, ToolsList).Verdict);
	}

	[Fact]
	public void BodyThatIsNotJsonNeedsASessionLikeAnyOther() =>
		Assert.Equal(McpHttpSessions.Verdict.BadRequest, new McpHttpSessions().Admit("POST", null, "{not json").Verdict);
}
