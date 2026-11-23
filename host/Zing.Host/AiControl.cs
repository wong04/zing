using Microsoft.UI.Dispatching;
using Zing.Core;
using Zing.Core.Mcp;

namespace Zing.Host;

/// <summary>
/// The app's local AI pieces, owned by ZingApp: shares, site levels, the confirm gate, the token, the tools, the JSON-RPC
/// layer and the endpoint. It follows the aiControlEnabled setting at runtime; off closes the endpoint and unshares every tab.
/// </summary>
public sealed class AiControl
{
	private readonly AiEndpoint _endpoint;
	private readonly McpJsonRpc _rpc;

	public AiControl(DispatcherQueue dispatcher, IScheduler scheduler, SitePolicy policy, AiToken token, string version, string pipeName)
	{
		Shares = new AiShares();
		Policy = policy;
		Gate = new ConfirmGate(scheduler, Shares, policy);
		Token = token;
		Tools = new AiTools(new NoPageDriver(), Shares, policy, Gate, scheduler);
		_rpc = new McpJsonRpc(Tools, version);
		// Stop() turns Tools off first, so a request already queued to the UI thread answers Disabled and runs nothing.
		_endpoint = new AiEndpoint(dispatcher, token, pipeName, message => McpText.Handle(_rpc.Handle, message, error => TestHooks.Record("ai request", error)));
	}

	public AiShares Shares { get; }
	public SitePolicy Policy { get; }
	public ConfirmGate Gate { get; }
	public AiToken Token { get; }
	public AiTools Tools { get; }

	/// <summary>What Settings shows when the endpoint couldn't fully start.</summary>
	public string? LastError => _endpoint.LastError;

	/// <summary>On the UI thread, at launch and on each change to the two settings.</summary>
	public void Follow(bool enabled, int port)
	{
		if (!enabled)
		{
			Stop();
			return;
		}
		Tools.Enabled = true;
		_endpoint.Start(port);
	}

	public void Stop()
	{
		Tools.Enabled = false;
		_endpoint.Stop();
		Shares.UnshareAll();
	}
}
