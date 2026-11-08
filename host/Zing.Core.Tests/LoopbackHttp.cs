using System.Net;
using System.Net.Sockets;

namespace Zing.Core.Tests;

/// <summary>An HttpListener on a loopback port nobody else has registered.</summary>
public static class LoopbackHttp
{
	private const int ErrorAlreadyExists = 183;
	private const int Attempts = 20;

	/// <summary>
	/// Winsock's port 0 can't be used: HttpListener listens through http.sys, whose registrations Winsock doesn't see, so a
	/// probed "free" port was now and then one another fixture had registered ("conflicts with an existing registration").
	/// http.sys only says so when the prefix is added, so a taken port is the cue to try the next.
	/// </summary>
	public static (HttpListener Listener, int Port) Start()
	{
		for (var attempt = 1; ; attempt++)
		{
			var port = ProbedPort();
			var listener = new HttpListener();
			listener.Prefixes.Add($"http://127.0.0.1:{port}/");
			try
			{
				listener.Start();
				return (listener, port);
			}
			catch (HttpListenerException error) when (error.ErrorCode == ErrorAlreadyExists && attempt < Attempts)
			{
				listener.Close();
			}
		}
	}

	private static int ProbedPort()
	{
		var probe = new TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		var port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		return port;
	}
}
