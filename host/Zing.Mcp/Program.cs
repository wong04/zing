using System.IO.Pipes;
using Zing.Core.Mcp;

// zing-mcp.exe --profile <key>: relays stdio to the running Zing's MCP pipe. It never starts Zing.
var profile = args is ["--profile", { Length: > 0 } key] ? key : null;
if (profile is null)
{
	Console.Error.WriteLine("usage: zing-mcp --profile <key>");
	return 2;
}

return await PipeRelay.Run(Console.OpenStandardInput(), Console.OpenStandardOutput(), () => Connect(profile), CancellationToken.None);

// CurrentUserOnly is what makes another user's server squatting on the name fail: the server's ACL can't carry it.
static NamedPipeClientStream? Connect(string profile)
{
	var pipe = new NamedPipeClientStream(".", McpPipeServer.Name(profile), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
	try
	{
		pipe.Connect(TimeSpan.FromSeconds(2));
		return pipe;
	}
	catch (TimeoutException)
	{
		pipe.Dispose();
		return null;
	}
}
