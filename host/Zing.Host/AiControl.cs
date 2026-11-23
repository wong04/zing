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

	public AiControl(DispatcherQueue dispatcher, IScheduler scheduler, SitePolicy policy, AiToken token, string version, string pipeName, Func<string, MainWindow?> windowOf, Func<MainWindow?> frontWindow)
	{
		Shares = new AiShares();
		Policy = policy;
		Gate = new ConfirmGate(scheduler, Shares, policy);
		Token = token;
		Backstop = new FormPostBackstop(policy, () => DateTime.UtcNow);
		Shares.Unshared += Backstop.Forget;
		Driver = new CdpPageDriver(windowOf, frontWindow, Shares, Gate, Backstop, scheduler, TestHooks.AiShowWait ?? CdpPageDriver.ShowWait);
		Tools = new AiTools(Driver, Shares, policy, Gate, scheduler);
		Tools.Acting += Backstop.Acted;
		_rpc = new McpJsonRpc(Tools, version);
		// Stop() turns Tools off first, so a request already queued to the UI thread answers Disabled and runs nothing.
		_endpoint = new AiEndpoint(dispatcher, token, pipeName, message => McpText.Handle(_rpc.Handle, message, error => TestHooks.Record("ai request", error)));
	}

	public AiShares Shares { get; }
	public SitePolicy Policy { get; }
	public ConfirmGate Gate { get; }
	public AiToken Token { get; }
	public AiTools Tools { get; }
	public CdpPageDriver Driver { get; }
	public FormPostBackstop Backstop { get; }

	/// <summary>
	/// Follows a window's tabs: every navigation and popup goes to the shares (an unshareable address unshares, and vault
	/// masks clear on a new origin), a closed tab is unshared, and shared tabs are kept awake.
	/// </summary>
	public void Watch(string windowId, TabService tabs)
	{
		tabs.KeptAwake = id => Shares.IsShared(new TabRef(windowId, id));
		tabs.PageNavigated += (id, url) => Navigated(new TabRef(windowId, id), url);
		tabs.PopupOpened += (opener, id, url) =>
		{
			if (Uri.TryCreate(url, UriKind.Absolute, out var to)) Shares.PopupFrom(new TabRef(windowId, opener), new TabRef(windowId, id), to);
		};
		tabs.Closed += id => Shares.Unshare(new TabRef(windowId, id));
	}

	/// <summary>A closing window's tabs go with it, shared or not.</summary>
	public void WindowClosed(string windowId)
	{
		foreach (var (_, tab) in Shares.List().Where(s => s.Tab.WindowId == windowId)) Shares.Unshare(tab);
	}

	/// <summary>Shares a tab as the tab menu will (Task 10); e2e shares through TestHooks' ai-share.</summary>
	public string Share(string windowId, TabService tabs, string tabId) => Shares.Share(new TabRef(windowId, tabId), new Uri(tabs.Get(tabId).Url));

	// An address that isn't one can't be checked as shareable, so a shared tab showing it is unshared.
	private void Navigated(TabRef tab, string url)
	{
		if (Uri.TryCreate(url, UriKind.Absolute, out var to)) Shares.Navigated(tab, to);
		else Shares.Unshare(tab);
	}

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
