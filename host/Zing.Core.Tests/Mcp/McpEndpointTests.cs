using System.Net;
using System.Security.Cryptography;
using Zing.Core.Mcp;

namespace Zing.Core.Tests.Mcp;

public sealed class McpEndpointTests
{
	private sealed class FakeListener(Func<Exception?> fail) : IMcpListener
	{
		public void Start()
		{
			if (fail() is { } error) throw error;
		}

		public void Stop()
		{
		}
	}

	private Exception? _httpFails;
	private Exception? _pipeFails;
	private readonly List<int> _httpPorts = [];
	private readonly McpEndpoint _endpoint;

	public McpEndpointTests() => _endpoint = new McpEndpoint(port =>
	{
		_httpPorts.Add(port);
		return new FakeListener(() => _httpFails);
	}, () => new FakeListener(() => _pipeFails), (_, _) => { });

	[Fact]
	public void PortInUseKeepsThePipeAndRetriesHttpOnTheNextEnsure()
	{
		_httpFails = new HttpListenerException(183);
		_endpoint.Ensure(47821);
		Assert.False(_endpoint.HttpRunning);
		Assert.True(_endpoint.PipeRunning);
		Assert.Contains("47821", _endpoint.LastError);

		_httpFails = null;
		_endpoint.Ensure(47821);
		Assert.True(_endpoint.HttpRunning);
		Assert.Null(_endpoint.LastError);
	}

	[Fact]
	public void ATokenThatCouldNotBeOpenedStartsHttpOnceRegenerated()
	{
		_httpFails = new CryptographicException();
		_endpoint.Ensure(47821);
		Assert.False(_endpoint.HttpRunning);

		_httpFails = null;
		_endpoint.TokenChanged();
		Assert.True(_endpoint.HttpRunning);
	}

	[Fact]
	public void TokenChangeWhileStoppedStartsNothing()
	{
		_endpoint.TokenChanged();
		Assert.Empty(_httpPorts);
	}

	[Fact]
	public void ATakenPipeIsRetried()
	{
		_pipeFails = new IOException();
		_endpoint.Ensure(47821);
		Assert.True(_endpoint.HttpRunning);
		Assert.False(_endpoint.PipeRunning);
		_pipeFails = null;
		_endpoint.Ensure(47821);
		Assert.True(_endpoint.PipeRunning);
		Assert.Equal([47821], _httpPorts);
	}

	[Fact]
	public void ANewPortRestartsHttp()
	{
		_endpoint.Ensure(1);
		_endpoint.Ensure(2);
		Assert.Equal([1, 2], _httpPorts);
	}

	[Fact]
	public void StopStopsBoth()
	{
		_endpoint.Ensure(1);
		_endpoint.Stop();
		Assert.False(_endpoint.HttpRunning);
		Assert.False(_endpoint.PipeRunning);
	}
}
