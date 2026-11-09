using System.Text.Json.Serialization;

namespace Zing.Core.Vault;

/// <summary>What the renderer may know about a login: everything but the password. Times are Unix milliseconds.</summary>
public sealed record LoginSummary(
	string Id,
	string Origin,
	string Username,
	string Title,
	string Note,
	long CreatedAt,
	long UpdatedAt,
	long PasswordUpdatedAt,
	// The renderer tests deletedAt === null, so it must be an explicit null, not an absent key.
	[property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? DeletedAt,
	// How many earlier passwords the history holds, so the manage page can offer them without asking for the list per login.
	int HistoryCount = 0);

/// <summary>"Never for this site", kept per exact origin apart from the logins.</summary>
public sealed record NeverSave(string Origin);

public sealed record FillContext(string TopUrl, string FrameUrl);

public enum SaveChoice { Save, Update, None }

public sealed class NotAWebOriginException(string message) : Exception(message);

/// <summary>The vault's pure rules. Source of truth for src/shared/vault/vault.ts, held to it by vault.vectors.json.</summary>
public static class VaultModel
{
	public const long RetentionMs = 30L * 24 * 60 * 60 * 1000;

	/// <summary>Exact origin, not the registrable domain: a password saved on accounts.example.com must not be offered to evil.example.com.</summary>
	public static string OriginOf(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new NotAWebOriginException($"Not a URL: {url}");
		if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) throw new NotAWebOriginException($"Only http(s) pages have saved passwords: {url}");
		// Built part by part, not from GetLeftPart: that keeps userinfo and a Unicode host, where URL.origin drops the one and punycodes the other.
		var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
		var host = uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.IdnHost}]" : uri.IdnHost;
		return $"{uri.Scheme}://{host}{port}";
	}

	/// <summary>Fill the main frame and same-origin frames only. A cross-origin frame is never offered anything.</summary>
	public static bool CanFill(FillContext context) => OriginOf(context.FrameUrl) == OriginOf(context.TopUrl);

	public static IReadOnlyList<LoginSummary> EntriesFor(IEnumerable<LoginSummary> summaries, FillContext context)
	{
		if (!CanFill(context)) return [];
		var origin = OriginOf(context.TopUrl);
		return [.. summaries.Where(s => s.DeletedAt is null && s.Origin == origin).OrderByDescending(s => s.UpdatedAt)];
	}

	/// <summary>
	/// The host compares the typed password with the stored one, since only it can read passwords, and passes the
	/// answer in. One login per (origin, username); a login in Recently deleted doesn't count as saved.
	/// </summary>
	public static SaveChoice SaveDecision(IEnumerable<LoginSummary> summaries, string origin, string username, bool passwordChanged, IEnumerable<NeverSave> neverSave)
	{
		if (neverSave.Any(n => n.Origin == origin)) return SaveChoice.None;
		var saved = summaries.Any(s => s.DeletedAt is null && s.Origin == origin && s.Username == username);
		if (!saved) return SaveChoice.Save;
		return passwordChanged ? SaveChoice.Update : SaveChoice.None;
	}

	public static IReadOnlyList<LoginSummary> RecentlyDeleted(IEnumerable<LoginSummary> summaries, long now) =>
		[.. summaries.Where(s => s.DeletedAt is not null && !DueForTombstone(s, now)).OrderByDescending(s => s.DeletedAt)];

	public static bool DueForTombstone(LoginSummary summary, long now) =>
		summary.DeletedAt is { } deletedAt && now - deletedAt >= RetentionMs;

	/// <summary>Holding any login, deleted ones included, or a "never for this site" mark (Ruling 39), is a stored record, so Settings' privacy banner warns.</summary>
	public static bool KeepsData(IReadOnlyCollection<LoginSummary> summaries, IReadOnlyCollection<NeverSave> neverSave) => summaries.Count > 0 || neverSave.Count > 0;
}
