using System.Text.Json;
using System.Text.Json.Nodes;
using Zing.Core;
using Zing.Core.Mcp;

namespace Zing.Host;

/// <summary>What a confirm bar asks; the description is page-influenced, so the view draws it as text only.</summary>
public sealed record AiConfirmView(string Tab, string Description, bool CanAlwaysAllow);

/// <summary>A window's tabs as AI control sees them (AiTabsState in ipc.ts).</summary>
public sealed record AiTabsView(IReadOnlyList<string> Shared, IReadOnlyList<string> Waiting, string? ActiveShared, AiConfirmView? Confirm);

public sealed record AiSiteView(string Site, string Level);

/// <summary>What Settings shows for AI control (AiSettingsState in ipc.ts). Never the token: Show asks for it (ai:show-token).</summary>
public sealed record AiSettingsView(bool Available, bool TokenStored, string? ClaudeCode, string? ClaudeDesktop, IReadOnlyList<AiSiteView> Sites, int SharedCount, string? Error);

/// <summary>
/// One window's share UI: the tab menu's share item, the outline and badge on its tabs (ai:state), the strip and the confirm
/// bar of the tab in front, and the Settings section. It follows the app's shares, confirm gate, site levels and token,
/// and lets go of them when the window's scope closes.
/// </summary>
public sealed class AiWindow : ITabSharing
{
	private readonly AiControl _ai;
	private readonly string _windowId;
	private readonly ChromeApi _api;
	private readonly Action<string, object?> _push;
	private readonly AiStripLayer _strip;
	private readonly AiConfirmLayer _confirm;
	private readonly Func<bool> _gone;
	// What each of this window's tabs' confirm bar asks, from the gate's Shown until its Closed.
	private readonly Dictionary<string, string> _asked = new(StringComparer.Ordinal);

	public AiWindow(AiControl ai, string windowId, ChromeApi api, WindowScope scope, Action<string, object?> push, AiStripLayer strip, AiConfirmLayer confirm, Func<bool> gone)
	{
		_gone = gone;
		_ai = ai;
		_windowId = windowId;
		_api = api;
		_push = push;
		_strip = strip;
		_confirm = confirm;
		Follow(scope, h => ai.Shares.Shared += h, h => ai.Shares.Shared -= h, (Action<TabRef>)Changed);
		Follow(scope, h => ai.Shares.Unshared += h, h => ai.Shares.Unshared -= h, (Action<TabRef>)Changed);
		Follow(scope, h => ai.Gate.Shown += h, h => ai.Gate.Shown -= h, (Action<TabRef, string>)Shown);
		Follow(scope, h => ai.Gate.Closed += h, h => ai.Gate.Closed -= h, (Action<TabRef>)Closed);
		Follow(scope, h => ai.Driver.ShowWaitChanged += h, h => ai.Driver.ShowWaitChanged -= h, (Action<TabRef>)Changed);
		Follow(scope, h => ai.Policy.Changed += h, h => ai.Policy.Changed -= h, (Action)Refresh);
		Follow(scope, h => ai.Token.Regenerated += h, h => ai.Token.Regenerated -= h, (Action)Refresh);
		api.TabsChanged += Refresh;
	}

	private static void Follow<T>(WindowScope scope, Action<T> add, Action<T> remove, T handler)
	{
		add(handler);
		scope.Track(() => remove(handler));
	}

	private TabRef Ref(string tabId) => new(_windowId, tabId);

	public bool IsShared(string tabId) => _ai.Shares.IsShared(Ref(tabId));

	public void Share(string tabId) => _ai.Share(_windowId, _api.Tabs, tabId);

	public void Unshare(string tabId) => _ai.Shares.Unshare(Ref(tabId));

	public void Answer(string tabId, string reply) => _ai.Gate.Answer(Ref(tabId), reply switch
	{
		"allow" => ConfirmAnswer.Allow,
		"deny" => ConfirmAnswer.Deny,
		"always" => ConfirmAnswer.AlwaysAllowSite,
		_ => throw new ArgumentException($"A confirm bar can't be answered '{reply}'", nameof(reply)),
	});

	public AiTabsView State()
	{
		var ids = _api.Tabs.Records.Select(r => r.Id).ToList();
		var active = _api.Tabs.ActiveId;
		var shared = ids.Where(IsShared).ToList();
		var waiting = ids.Where(id => id != active && (_asked.ContainsKey(id) || _ai.Driver.IsWaitingToBeShown(Ref(id)))).ToList();
		var activeShared = active is not null && IsShared(active) ? active : null;
		var confirm = active is not null && _asked.TryGetValue(active, out var asked) ? new AiConfirmView(active, asked, CanAlwaysAllow(active)) : null;
		return new AiTabsView(shared, waiting, activeShared, confirm);
	}

	// "Always allow on this site" sets the site's level, and an address without a site (localhost) has none to set.
	private bool CanAlwaysAllow(string tabId) => Uri.TryCreate(_api.Tabs.Get(tabId).Url, UriKind.Absolute, out var url) && SitePolicy.HasSite(url);

	/// <summary>Pushes the state to the window's views and shows or hides the bars; raised by app-wide events, so it never throws for a closed window.</summary>
	public void Refresh()
	{
		// A window being destroyed (quitting unshares every tab) has no views left to tell.
		if (_gone()) return;
		var state = State();
		_push("ai:state", state);
		_strip.Changed(state.ActiveShared is not null);
		_confirm.Changed(state.Confirm is not null);
	}

	public void StripSize(int width, int height)
	{
		_strip.SetSize(width, height);
		// The confirm bar sits under the strip.
		_confirm.Layout();
	}

	public void ConfirmSize(int width, int height) => _confirm.SetSize(width, height);

	public void Layout()
	{
		_strip.Layout();
		_confirm.Layout();
	}

	private void Changed(TabRef tab)
	{
		if (tab.WindowId == _windowId) Refresh();
	}

	private void Shown(TabRef tab, string description)
	{
		if (tab.WindowId != _windowId) return;
		_asked[tab.TabId] = description;
		Refresh();
	}

	private void Closed(TabRef tab)
	{
		if (tab.WindowId != _windowId) return;
		_asked.Remove(tab.TabId);
		Refresh();
	}

	/// <summary>The Settings section. It never carries the token, which reaches the sheet only on Show.</summary>
	public static AiSettingsView Settings(AiControl? ai, bool enabled, int port)
	{
		if (ai is null) return new AiSettingsView(false, File.Exists(AppPaths.AiToken), null, null, [], 0, null);
		var sites = ai.Policy.All().OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new AiSiteView(p.Key, p.Value.ToString())).ToList();
		return new AiSettingsView(true, File.Exists(AppPaths.AiToken), enabled ? ClaudeCode(port, TokenPlaceholder) : null, enabled ? ClaudeDesktop() : null, sites, ai.Shares.List().Count, enabled ? ai.LastError : null);
	}

	/// <summary>Stands for the token in the snippet Settings shows; Show and Copy put the real one in.</summary>
	public const string TokenPlaceholder = "<click Show>";

	/// <summary>The token, read (made, on first use) only while AI control is on.</summary>
	public static string Token(AiControl ai, bool enabled) =>
		enabled ? AiRequestCheck.Encode(ai.Token.Current()) : throw new InvalidOperationException("Turn on AI control to see its token");

	// 127.0.0.1, not localhost: the listener turns away any other Host.
	public static string ClaudeCode(int port, string token) => $"claude mcp add --transport http zing http://127.0.0.1:{port}/mcp/ --header \"Authorization: Bearer {token}\"";

	/// <summary>The Claude desktop config's entry: the stdio shim next to Zing.exe, for this profile's pipe.</summary>
	public static string ClaudeDesktop()
	{
		var entry = new JsonObject
		{
			["mcpServers"] = new JsonObject
			{
				["zing"] = new JsonObject
				{
					["command"] = Path.Combine(AppContext.BaseDirectory, "zing-mcp.exe"),
					["args"] = new JsonArray("--profile", SingleInstance.ProfileKey),
				},
			},
		};
		return entry.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
	}

	/// <summary>A site from the Settings list (a registrable domain) as an address SitePolicy keys the same way.</summary>
	public static void SetSiteLevel(AiControl ai, string site, string level)
	{
		if (!Enum.GetNames<SiteLevel>().Contains(level)) throw new ArgumentException($"Unknown AI site level '{level}'", nameof(level));
		var url = new Uri($"https://{site}/");
		ai.Policy.Set(url, Enum.Parse<SiteLevel>(level));
	}
}
