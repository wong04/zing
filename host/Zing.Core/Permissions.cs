namespace Zing.Core;

/// <summary>The engine's pending request. The host implements it over its deferral/callback.</summary>
public interface IPermissionResolver
{
	void Resolve(bool granted);
}

public sealed record PermissionDetails(string? RequestingUrl = null, IReadOnlyList<string>? MediaTypes = null);

/// <summary>
/// Port of src/main/permissions.ts. Camera, microphone, location and notifications are asked
/// for in the page UI; other permissions are refused except the few ordinary pages need. A
/// grant lasts as long as the page (tab), never longer. Permission names are Electron's
/// ('media' with <c>MediaTypes</c> 'video'/'audio', 'geolocation', 'notifications', ...); the
/// host maps WebView2's kinds onto them.
/// </summary>
public sealed class PermissionBroker(PageUiStore ui)
{
	private static readonly IReadOnlySet<string> AlwaysAllowed = new HashSet<string> { "fullscreen", "clipboard-sanitized-write", "pointerLock" };

	private sealed record Pending(int Id, object Page, string Origin, string Kind, IReadOnlyList<string> Grants, IPermissionResolver Resolver);

	private readonly Dictionary<object, HashSet<string>> _granted = new(ReferenceEqualityComparer.Instance);
	private List<Pending> _queue = [];
	private int _nextId = 1;

	public static bool IsAlwaysAllowed(string permission) => AlwaysAllowed.Contains(permission);

	/// <summary>Call on a main-frame navigation to a new document, and when the page closes.</summary>
	public void DropFor(object page)
	{
		var dropped = _queue.Where(p => ReferenceEquals(p.Page, page)).ToList();
		_granted.Remove(page);
		if (dropped.Count == 0) return;
		_queue = [.. _queue.Where(p => !ReferenceEquals(p.Page, page))];
		foreach (var pending in dropped) pending.Resolver.Resolve(false);
		ShowNext();
	}

	public void Request(object page, string pageUrl, string permission, PermissionDetails details, IPermissionResolver resolver)
	{
		if (AlwaysAllowed.Contains(permission))
		{
			resolver.Resolve(true);
			return;
		}
		var origin = OriginOf(details.RequestingUrl ?? pageUrl);
		var types = details.MediaTypes ?? [];
		var kind = KindOf(permission, types);
		if (kind is null)
		{
			resolver.Resolve(false);
			return;
		}
		var grants = GrantKeys(origin, permission, types);
		if (_granted.TryGetValue(page, out var held) && grants.All(held.Contains))
		{
			resolver.Resolve(true);
			return;
		}
		_queue.Add(new Pending(_nextId++, page, origin, kind, grants, resolver));
		ShowNext();
	}

	public bool Check(object? page, string permission, string origin, string? mediaType)
	{
		if (AlwaysAllowed.Contains(permission)) return true;
		if (page is null) return false;
		var key = permission == "media" ? $"{origin}|media:{mediaType}" : $"{origin}|{permission}";
		return _granted.TryGetValue(page, out var held) && held.Contains(key);
	}

	/// <summary>`permission:allow` / `permission:deny`. An id no longer queued (the page navigated away first) is ignored.</summary>
	public void Answer(int id, bool allow)
	{
		var at = _queue.FindIndex(p => p.Id == id);
		if (at == -1) return;
		var pending = _queue[at];
		_queue.RemoveAt(at);
		if (allow) Remember(pending);
		pending.Resolver.Resolve(allow);
		ShowNext();
	}

	private void Remember(Pending pending)
	{
		if (!_granted.TryGetValue(pending.Page, out var held)) _granted[pending.Page] = held = [];
		foreach (var key in pending.Grants) held.Add(key);
	}

	private void ShowNext()
	{
		var next = _queue.FirstOrDefault();
		ui.Update(s => s with { Permission = next is null ? null : new PermissionPrompt(next.Id, next.Origin, next.Kind) });
	}

	// Like JS `URL.origin`: only http(s) pages have a usable one.
	private static string OriginOf(string? url)
	{
		if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "this page";
		return uri.Scheme is "http" or "https" ? uri.GetLeftPart(UriPartial.Authority) : "null";
	}

	private static string? MediaKind(IReadOnlyList<string> types)
	{
		var video = types.Contains("video");
		var audio = types.Contains("audio");
		if (video && audio) return PermissionKinds.CameraAndMicrophone;
		if (video) return PermissionKinds.Camera;
		return audio ? PermissionKinds.Microphone : null;
	}

	private static string? KindOf(string permission, IReadOnlyList<string> types) => permission switch
	{
		"media" => MediaKind(types),
		"geolocation" => PermissionKinds.Location,
		"notifications" => PermissionKinds.Notifications,
		_ => null,
	};

	private static List<string> GrantKeys(string origin, string permission, IReadOnlyList<string> types) =>
		permission == "media" ? [.. types.Select(t => $"{origin}|media:{t}")] : [$"{origin}|{permission}"];
}
