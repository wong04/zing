using System.Security.Cryptography;

namespace Zing.Core.Mcp;

public readonly record struct TabRef(string WindowId, string TabId);

public sealed class NotShareableException(string scheme)
	: InvalidOperationException($"Pages with the '{scheme}' scheme can't be shared with an AI client.");

/// <summary>The tabs the user has shared with a local AI client. Clients only ever see the opaque aiId, never Zing's tab id.</summary>
public sealed class AiShares
{
	private const string IdAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
	private const int IdLength = 12;

	private readonly Dictionary<TabRef, string> _byTab = [];
	private readonly Dictionary<string, TabRef> _byId = [];

	public event Action<TabRef>? Shared;
	public event Action<TabRef>? Unshared;

	public string Share(TabRef tab, Uri url)
	{
		if (!AiUrls.Allowed(url)) throw new NotShareableException(url.Scheme);
		if (_byTab.TryGetValue(tab, out var existing)) return existing;
		var aiId = NewId();
		_byTab[tab] = aiId;
		_byId[aiId] = tab;
		Shared?.Invoke(tab);
		return aiId;
	}

	public void Unshare(TabRef tab)
	{
		if (!_byTab.Remove(tab, out var aiId)) return;
		_byId.Remove(aiId);
		Unshared?.Invoke(tab);
	}

	public void UnshareAll()
	{
		foreach (var tab in _byTab.Keys.ToList()) Unshare(tab);
	}

	public bool TryResolve(string aiId, out TabRef tab) => _byId.TryGetValue(aiId, out tab);

	public bool IsShared(TabRef tab) => _byTab.ContainsKey(tab);

	/// <summary>Raised for every top-level navigation the host reports, shared tab or not, before any unshare it causes.</summary>
	public event Action<TabRef, Uri>? NavigatedTo;

	public void Navigated(TabRef tab, Uri url)
	{
		NavigatedTo?.Invoke(tab, url);
		if (!AiUrls.Allowed(url)) Unshare(tab);
	}

	public void PopupFrom(TabRef opener, TabRef popup, Uri url)
	{
		if (!IsShared(opener) || !AiUrls.Allowed(url)) return;
		Share(popup, url);
	}

	public IReadOnlyList<(string AiId, TabRef Tab)> List() => _byTab.Select(p => (p.Value, p.Key)).ToList();

	private string NewId()
	{
		while (true)
		{
			var aiId = RandomNumberGenerator.GetString(IdAlphabet, IdLength);
			if (!_byId.ContainsKey(aiId)) return aiId;
		}
	}
}
