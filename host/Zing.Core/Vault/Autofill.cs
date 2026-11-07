using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zing.Core.Vault;

public enum CredentialFieldKind { Username, Password }

/// <summary>
/// The focused sign-in field. <see cref="Rect"/> is in the page's CSS pixels as the script reports it, and in DIPs of
/// the window's content once the page has placed it; <see cref="Viewport"/> is then the page's own bounds there, which
/// the picker is kept inside. The script never reports a viewport (it's <c>default</c> until the page places the field).
/// </summary>
public sealed record CredentialField(CredentialFieldKind Kind, Rect Rect, Rect Viewport);

/// <summary>
/// The picker's keys. The arrows and Esc come only from real key presses the host sees through the controller's
/// AcceleratorKeyPressed, never from the page (Ruling 28). Plain Enter can't come that way: it maps to a character, so it isn't
/// an accelerator key to WebView2. It comes through the page channel instead (Ruling 46, <see cref="AutofillMessage.PickerEnter"/>),
/// and counts only while <see cref="Autofill.TakesEnter"/>.
/// </summary>
public enum PickerKey { Up, Down, Enter, Escape }

public static class PickerKeys
{
	/// <summary>The picker key for a Windows virtual-key code; null for any other key, which stays the page's.</summary>
	public static PickerKey? FromVirtualKey(int virtualKey) => virtualKey switch
	{
		0x26 => PickerKey.Up,
		0x28 => PickerKey.Down,
		0x0D => PickerKey.Enter,
		0x1B => PickerKey.Escape,
		_ => null,
	};
}

/// <summary>vault:fill named a login the picker on screen doesn't offer.</summary>
public sealed class AutofillEntryException(string id) : Exception($"The account picker doesn't offer login {id}");

/// <summary>What became of a message a page posted.</summary>
public enum PageMessageVerdict
{
	/// <summary>Not under the script's key: pages may post anything, and it isn't autofill's.</summary>
	NotAutofill,
	/// <summary>Too big, too deep, not JSON, or under the script's key but not a shape the script sends: dropped (Ruling 29).</summary>
	Dropped,
	Autofill,
}

/// <summary>
/// What the detection script (src/shared/vault/autofillScript.ts) posts: <c>{zingCredentialField: {kind, x, y, w, h}}</c>
/// or <c>{kind: "none"}</c>. It never carries an origin the host would believe: a page can post exactly the same thing,
/// so the origin always comes from WebView2's own <c>Source</c> for the message. Nor keys: those the host reads itself.
/// </summary>
public abstract record AutofillMessage
{
	/// <summary>The longest page message read at all; the script's are under 100 characters.</summary>
	public const int MaxLength = 4096;

	/// <summary>The script's messages are two objects deep; anything deeper isn't one, and isn't parsed further.</summary>
	private static readonly JsonDocumentOptions Options = new() { MaxDepth = 4 };

	private AutofillMessage() { }

	public sealed record Focused(CredentialField Field) : AutofillMessage;

	/// <summary>No sign-in field has focus any more, or the page started a new document.</summary>
	public sealed record Left() : AutofillMessage;

	/// <summary>A sign-in form was submitted. Whatever the page says is only what it says: SignInWatch holds it for its own origin.</summary>
	public sealed record Submitted(string Username, string Password) : AutofillMessage;

	/// <summary>The page now shows (or no longer shows) a visible password box.</summary>
	public sealed record Landed(bool HasVisiblePasswordBox) : AutofillMessage;

	/// <summary>
	/// Enter, which the script took from the page because the host told it an entry is highlighted (Ruling 46). Any page can post
	/// it, so the host acts on it only while <see cref="Autofill.TakesEnter"/> holds for that page and origin.
	/// </summary>
	public sealed record PickerEnter() : AutofillMessage;

	/// <summary>Reads a page message. Never throws: any page can post anything (Ruling 29).</summary>
	public static PageMessageVerdict Read(string json, out AutofillMessage? message)
	{
		message = null;
		if (json.Length > MaxLength) return PageMessageVerdict.Dropped;
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(json, Options);
		}
		catch (JsonException)
		{
			return PageMessageVerdict.Dropped;
		}
		using (document)
		{
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(AutofillScripts.MessageKey, out var body)) return PageMessageVerdict.NotAutofill;
			message = Shape(body);
			return message is null ? PageMessageVerdict.Dropped : PageMessageVerdict.Autofill;
		}
	}

	/// <summary>The message the body is, or null when it isn't one the script sends.</summary>
	private static AutofillMessage? Shape(JsonElement body)
	{
		if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String) return null;
		CredentialFieldKind field;
		switch (kind.GetString())
		{
			case "none":
				return new Left();
			case "submit":
				return Text(body, "username", SignInWatch.UsernameMax) is { Length: > 0 } user && Text(body, "password", SignInWatch.PasswordMax) is { Length: > 0 } password ? new Submitted(user, password) : null;
			case "landed":
				return body.TryGetProperty("password", out var box) && box.ValueKind is JsonValueKind.True or JsonValueKind.False ? new Landed(box.GetBoolean()) : null;
			case "picker-enter":
				return new PickerEnter();
			case "username":
				field = CredentialFieldKind.Username;
				break;
			case "password":
				field = CredentialFieldKind.Password;
				break;
			default:
				return null;
		}
		if (Number(body, "x") is not { } x || Number(body, "y") is not { } y || Number(body, "w") is not { } w || Number(body, "h") is not { } h) return null;
		// A field with no size has nothing to show a picker under.
		if (w <= 0 || h <= 0) return new Left();
		return new Focused(new CredentialField(field, new Rect(x, y, w, h), default));
	}

	/// <summary>A string property of at most <paramref name="max"/> UTF-16 code units; null when it is absent, not a string or longer.</summary>
	private static string? Text(JsonElement body, string name, int max) =>
		body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.Length <= max ? text : null;

	private static double? Number(JsonElement body, string name) =>
		body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
}

/// <summary>The page scripts, from Defaults/autofill.json, which is generated from autofillScript.ts.</summary>
public static class AutofillScripts
{
	private static readonly Lazy<JsonObject> Defaults = new(() => ZingJson.LoadDefaults("autofill.json").AsObject());

	private static string Value(string key) => Defaults.Value[key]?.GetValue<string>() ?? throw new InvalidDataException($"autofill.json has no {key}");

	/// <summary>Injected into every document; installs itself in the top frame only.</summary>
	public static string Detection => Value("detectionScript");

	public static string MessageKey => Value("messageKey");

	/// <summary>
	/// Tells the page's script whether Enter is the picker's now (Ruling 46): while true, it takes Enter from the page and posts
	/// picker-enter. Telling it is no permission: the host still checks <see cref="Autofill.TakesEnter"/> on every message.
	/// </summary>
	public static string PickerEnter(bool on) => $"window.{Value("pickerEnterFlag")} = {(on ? "true" : "false")}";

	/// <summary>
	/// fillScript's call: fills the reported field and its other half, and evaluates to whether it did. It runs only in a
	/// document still on <paramref name="origin"/>, so a navigation that commits after the host's last check gets nothing.
	/// </summary>
	public static string Fill(string origin, string username, string password) =>
		$"location.origin === {JsonSerializer.Serialize(origin)} && window.{Value("fillFunction")}({JsonSerializer.Serialize(username)}, {JsonSerializer.Serialize(password)})";
}

public sealed record PickerEntry(string Id, string Username);

/// <summary>
/// Pushed as pageui:account-picker. Usernames only: the picker never shows a password, and the page never sees any of it.
/// <see cref="Active"/> is the entry the arrows have selected, -1 for none: it opens with nothing selected.
/// </summary>
public sealed record AccountPicker(string PageId, Rect Rect, Rect Viewport, IReadOnlyList<PickerEntry> Entries, int Active);

public static class FillOutcomes
{
	public const string Filled = "filled";
	/// <summary>The field had gone from the page by the time Hello passed.</summary>
	public const string NotFilled = "not-filled";
	/// <summary>Windows Hello was cancelled or is unavailable: nothing filled, and it isn't an error.</summary>
	public const string Cancelled = "cancelled";
	/// <summary>The page is no longer on the origin the picker was shown for (canFill).</summary>
	public const string Refused = "refused";
	/// <summary>A click on a picker that had been on screen, where it is, for less than the click guard: a page may have moved it under the pointer.</summary>
	public const string Ignored = "ignored";
}

/// <summary>
/// One window's account picker and fill. The picker's origin is the frame's actual source as WebView2 reports it, never
/// anything the page sent; a frame whose origin isn't the top page's gets nothing (<see cref="VaultModel.CanFill"/>).
/// A password is read, with one Hello prompt, only once the user has chosen a login with a click or a real Enter, and
/// goes only into the page whose origin the picker was shown for.
/// </summary>
/// <param name="vault">The vault; null while it's off.</param>
/// <param name="pageOf">The page of a tab that is on screen; null for one that isn't, or has gone.</param>
/// <param name="changed">The picker opened, changed or closed (null).</param>
/// <param name="background">Runs a fill nobody awaits (a real Enter), reporting its failure.</param>
/// <param name="filled">A login was filled into a page (page ID, origin, username, password), so signing in with it is not a changed password.</param>
public sealed class Autofill(Func<VaultStore?> vault, Func<string, IPage?> pageOf, Action<AccountPicker?> changed, IClock clock, IScheduler scheduler, Action<Task, string> background, Action<string, string, string, string> filled) : IDisposable
{
	/// <summary>At most one picker update per page in this long; the latest report wins (Ruling 29).</summary>
	public static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(100);

	/// <summary>A click counts only once the picker has been on screen, where it is, this long (Ruling 28).</summary>
	public const long ClickGuardMs = 500;

	private sealed record Open(AccountPicker Picker, IPage Page, string Origin, long ShownAt);

	private sealed class Window
	{
		public IDisposable? Timer;
		public (Uri FrameSource, CredentialField Field)? Pending;
	}

	private Open? _open;
	/// <summary>The page told Enter is the picker's (Ruling 46), or null.</summary>
	private IPage? _toldEnter;
	private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);

	public AccountPicker? Picker => _open?.Picker;

	/// <summary>
	/// Whether Enter, pressed now, is the picker's and must not reach the page (Ruling 45): the picker is open with an entry
	/// highlighted, which only a real arrow key does, so a page alone can never make it true. The picker's page is told, so
	/// its script takes Enter from the page, and the host checks this again on the script's picker-enter.
	/// </summary>
	public bool TakesEnter => _open is { Picker.Active: >= 0 };

	/// <summary>A message from a page's script; <paramref name="frameSource"/> is WebView2's Source for it.</summary>
	public void Message(string pageId, Uri frameSource, AutofillMessage message)
	{
		switch (message)
		{
			case AutofillMessage.Focused focused:
				FieldFocused(pageId, frameSource, focused.Field);
				return;
			case AutofillMessage.Left:
				if (_windows.TryGetValue(pageId, out var window)) window.Pending = null;
				CloseFor(pageId);
				return;
			case AutofillMessage.PickerEnter:
				PageEnter(pageId, frameSource);
				return;
		}
	}

	/// <summary>
	/// Enter the page's script took (Ruling 46). It counts only for the page and origin the picker is open on, and only while
	/// an entry is highlighted, which only a real arrow key does; then it is Enter on the picker, guard and all. Anything else
	/// (no picker, nothing highlighted, another page or frame) does nothing: a page alone can never choose.
	/// </summary>
	private void PageEnter(string pageId, Uri frameSource)
	{
		if (_open is not { } open || !TakesEnter || open.Picker.PageId != pageId || !ReferenceEquals(pageOf(pageId), open.Page)) return;
		if (!IsWeb(frameSource) || VaultModel.OriginOf(frameSource.AbsoluteUri) != open.Origin) return;
		Key(PickerKey.Enter);
	}

	/// <summary>Shows the picker for the logins of the frame's actual origin, or closes it when there are none to offer; throttled per page.</summary>
	public void FieldFocused(string pageId, Uri frameSource, CredentialField field)
	{
		if (_windows.TryGetValue(pageId, out var window))
		{
			window.Pending = (frameSource, field);
			return;
		}
		Show(pageId, frameSource, field);
		StartWindow(pageId);
	}

	private void StartWindow(string pageId)
	{
		var window = new Window();
		_windows[pageId] = window;
		window.Timer = scheduler.Schedule(Throttle, () => EndWindow(pageId, window));
	}

	private void EndWindow(string pageId, Window window)
	{
		if (!_windows.TryGetValue(pageId, out var current) || !ReferenceEquals(current, window)) return;
		_windows.Remove(pageId);
		if (window.Pending is not { } pending) return;
		Show(pageId, pending.FrameSource, pending.Field);
		StartWindow(pageId);
	}

	/// <summary>The tabs changed: a picker whose tab has left the screen goes.</summary>
	public void PagesChanged()
	{
		if (_open is { } open && !ReferenceEquals(pageOf(open.Picker.PageId), open.Page)) Close();
	}

	/// <summary>Closed from the picker: the page gets the keyboard back.</summary>
	public void Dismiss()
	{
		if (_open is not { } open) return;
		Close();
		open.Page.Focus();
	}

	/// <summary>
	/// A real key press while the picker is open, from its page or the picker. True when the picker took it (the page
	/// mustn't get it); false when it stays the page's: no picker, or Enter with nothing selected.
	/// </summary>
	public bool Key(PickerKey key)
	{
		if (_open is not { } open) return false;
		var count = open.Picker.Entries.Count;
		switch (key)
		{
			case PickerKey.Down:
				Select(open, (open.Picker.Active + 1) % count);
				return true;
			case PickerKey.Up:
				Select(open, open.Picker.Active <= 0 ? count - 1 : open.Picker.Active - 1);
				return true;
			case PickerKey.Escape:
				Close();
				return true;
			case PickerKey.Enter:
				if (open.Picker.Active < 0) return false;
				// Ruling 31: like a click, Enter counts only once the picker has been on screen, unmoved, for the guard. Before
				// then it is still taken from the page (it was aimed at the picker's selection, and a page form submitting
				// half-filled would surprise more), but does nothing.
				if (!Settled(open)) return true;
				background(ChooseAsync(open, open.Picker.Entries[open.Picker.Active].Id), "autofill");
				return true;
			default:
				throw new ArgumentOutOfRangeException(nameof(key), key, "not a picker key");
		}
	}

	private void Select(Open open, int active)
	{
		_open = open with { Picker = open.Picker with { Active = active } };
		Changed();
	}

	/// <summary>A click on a login in the picker.</summary>
	/// <exception cref="AutofillEntryException">The picker on screen doesn't offer <paramref name="id"/>.</exception>
	public async Task<string> FillAsync(string pageId, string id)
	{
		// A choice that arrives after the picker closed (the page moved on meanwhile) has nothing to fill.
		if (_open is not { } open || open.Picker.PageId != pageId) return FillOutcomes.Refused;
		if (!open.Picker.Entries.Any(e => e.Id == id)) throw new AutofillEntryException(id);
		if (!Settled(open)) return FillOutcomes.Ignored;
		return await ChooseAsync(open, id);
	}

	/// <summary>The picker has been on screen, where it is, for the guard: a page can't move it under a click or a quick Enter.</summary>
	private bool Settled(Open open) => clock.Now() - open.ShownAt >= ClickGuardMs;

	private async Task<string> ChooseAsync(Open open, string id)
	{
		Close();
		open.Page.Focus();
		if (!StillOn(open)) return FillOutcomes.Refused;
		var store = vault() ?? throw new VaultOffException();
		var login = store.Summaries().FirstOrDefault(s => s.Id == id && s.DeletedAt is null && s.Origin == open.Origin) ?? throw new VaultItemNotFoundException(id);
		string password;
		try
		{
			password = await store.PasswordAsync(id);
		}
		catch (Exception e) when (e is HelloCancelledException or HelloUnavailableException)
		{
			// Review Focus 3: nothing is filled and there's no error to show; the picker has already closed.
			return FillOutcomes.Cancelled;
		}
		// The page may have gone elsewhere while Hello was up, or the vault been turned off (or off and on, a new store);
		// the script checks the origin once more as it runs.
		if (!StillOn(open) || !ReferenceEquals(vault(), store)) return FillOutcomes.Refused;
		var done = await open.Page.EvaluateAsync(AutofillScripts.Fill(open.Origin, login.Username, password));
		if (done.ValueKind != JsonValueKind.True) return FillOutcomes.NotFilled;
		filled(open.Picker.PageId, open.Origin, login.Username, password);
		return FillOutcomes.Filled;
	}

	private void Show(string pageId, Uri frameSource, CredentialField field)
	{
		if (vault() is not { } store || pageOf(pageId) is not { } page || !IsWeb(frameSource) || !Uri.TryCreate(page.Url, UriKind.Absolute, out var top) || !IsWeb(top))
		{
			CloseFor(pageId);
			return;
		}
		var context = new FillContext(top.AbsoluteUri, frameSource.AbsoluteUri);
		List<PickerEntry> entries = [.. VaultModel.EntriesFor(store.Summaries(), context).Select(e => new PickerEntry(e.Id, e.Username))];
		if (entries.Count == 0)
		{
			CloseFor(pageId);
			return;
		}
		var same = _open is { } was && was.Picker.PageId == pageId && ReferenceEquals(was.Page, page);
		var unmoved = same && _open!.Picker.Rect == field.Rect && _open.Picker.Viewport == field.Viewport;
		var active = same && _open!.Picker.Entries.SequenceEqual(entries) ? _open.Picker.Active : -1;
		var shownAt = unmoved ? _open!.ShownAt : clock.Now();
		_open = new Open(new AccountPicker(pageId, field.Rect, field.Viewport, entries, active), page, VaultModel.OriginOf(frameSource.AbsoluteUri), shownAt);
		Changed();
	}

	/// <summary>canFill against the origin the entries were chosen for, with the page's source now.</summary>
	private bool StillOn(Open open) =>
		ReferenceEquals(pageOf(open.Picker.PageId), open.Page)
		&& Uri.TryCreate(open.Page.Url, UriKind.Absolute, out var now) && IsWeb(now)
		&& VaultModel.CanFill(new FillContext(open.Origin, now.AbsoluteUri));

	private static bool IsWeb(Uri uri) => uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp;

	private void CloseFor(string pageId)
	{
		if (_open?.Picker.PageId == pageId) Close();
	}

	/// <summary>Closes the picker, if one is open; the vault turning off does this.</summary>
	public void Close()
	{
		if (_open is null) return;
		_open = null;
		Changed();
	}

	private void Changed()
	{
		changed(_open?.Picker);
		TellEnter();
	}

	/// <summary>Tells the page whether its script should take Enter: only the picker's page, only while an entry is highlighted.</summary>
	private void TellEnter()
	{
		var page = TakesEnter ? _open!.Page : null;
		if (ReferenceEquals(page, _toldEnter)) return;
		_toldEnter?.Run(AutofillScripts.PickerEnter(false));
		page?.Run(AutofillScripts.PickerEnter(true));
		_toldEnter = page;
	}

	public void Dispose()
	{
		foreach (var window in _windows.Values) window.Timer?.Dispose();
		_windows.Clear();
	}
}
