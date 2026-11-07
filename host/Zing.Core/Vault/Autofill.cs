using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zing.Core.Vault;

public enum CredentialFieldKind { Username, Password }

/// <summary>The focused sign-in field. <see cref="Rect"/> is in the page's CSS pixels as the script reports it, and in DIPs of the window's content once the page has placed it.</summary>
public sealed record CredentialField(CredentialFieldKind Kind, Rect Rect);

public enum PickerKey { Down, Escape }

/// <summary>A message under the script's key that isn't one the script sends: a page forged it, or the script and host disagree.</summary>
public sealed class AutofillMessageException(string message) : Exception(message);

/// <summary>vault:fill named a login the picker on screen doesn't offer.</summary>
public sealed class AutofillEntryException(string id) : Exception($"The account picker doesn't offer login {id}");

/// <summary>
/// What the detection script (src/shared/vault/autofillScript.ts) posts: <c>{zingCredentialField: {kind, x, y, w, h}}</c>,
/// <c>{kind: "none"}</c> or <c>{kind: "key", key}</c>. It never carries an origin the host would believe: a page can post
/// exactly the same thing, so the origin always comes from WebView2's own <c>Source</c> for the message.
/// </summary>
public abstract record AutofillMessage
{
	private AutofillMessage() { }

	public sealed record Focused(CredentialField Field) : AutofillMessage;

	/// <summary>No sign-in field has focus any more, or the page started a new document.</summary>
	public sealed record Left() : AutofillMessage;

	public sealed record Key(PickerKey Pressed) : AutofillMessage;

	/// <summary>Null for a message that isn't under the script's key (pages may post anything); throws for one that is but makes no sense.</summary>
	/// <exception cref="AutofillMessageException"/>
	public static AutofillMessage? Parse(string json)
	{
		using var document = JsonDocument.Parse(json);
		var root = document.RootElement;
		if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(AutofillScripts.MessageKey, out var body)) return null;
		if (body.ValueKind != JsonValueKind.Object) throw new AutofillMessageException("autofill message is not an object");
		var kind = body.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
		switch (kind)
		{
			case "none":
				return new Left();
			case "key":
				return new Key(body.TryGetProperty("key", out var key) ? key.GetString() switch
				{
					"down" => PickerKey.Down,
					"escape" => PickerKey.Escape,
					var other => throw new AutofillMessageException($"unknown picker key: {other}"),
				} : throw new AutofillMessageException("autofill key message has no key"));
			case "username":
			case "password":
				var (x, y, w, h) = (Number(body, "x"), Number(body, "y"), Number(body, "w"), Number(body, "h"));
				// A field with no size has nothing to show a picker under.
				if (w <= 0 || h <= 0) return new Left();
				return new Focused(new CredentialField(kind == "username" ? CredentialFieldKind.Username : CredentialFieldKind.Password, new Rect(x, y, w, h)));
			default:
				throw new AutofillMessageException($"unknown autofill field kind: {kind}");
		}
	}

	private static double Number(JsonElement body, string name)
	{
		if (!body.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) throw new AutofillMessageException("autofill message has no usable position");
		var number = value.GetDouble();
		return double.IsFinite(number) ? number : throw new AutofillMessageException("autofill message has no usable position");
	}
}

/// <summary>The page scripts, from Defaults/autofill.json, which is generated from autofillScript.ts.</summary>
public static class AutofillScripts
{
	private static readonly Lazy<JsonObject> Defaults = new(() => ZingJson.LoadDefaults("autofill.json").AsObject());

	private static string Value(string key) => Defaults.Value[key]?.GetValue<string>() ?? throw new InvalidDataException($"autofill.json has no {key}");

	/// <summary>Injected into every document; installs itself in the top frame only.</summary>
	public static string Detection => Value("detectionScript");

	public static string MessageKey => Value("messageKey");

	/// <summary>fillScript's call: fills the reported field and its other half, and evaluates to whether it did.</summary>
	public static string Fill(string username, string password) =>
		$"window.{Value("fillFunction")}({JsonSerializer.Serialize(username)}, {JsonSerializer.Serialize(password)})";
}

public sealed record PickerEntry(string Id, string Username);

/// <summary>Pushed as pageui:account-picker. Usernames only: the picker never shows a password, and the page never sees any of it.</summary>
public sealed record AccountPicker(string PageId, Rect Rect, IReadOnlyList<PickerEntry> Entries);

public static class FillOutcomes
{
	public const string Filled = "filled";
	/// <summary>The field had gone from the page by the time Hello passed.</summary>
	public const string NotFilled = "not-filled";
	/// <summary>Windows Hello was cancelled or is unavailable: nothing filled, and it isn't an error.</summary>
	public const string Cancelled = "cancelled";
	/// <summary>The page is no longer on the origin the picker was shown for (canFill).</summary>
	public const string Refused = "refused";
}

/// <summary>
/// One window's account picker and fill. The picker's origin is the frame's actual source as WebView2 reports it, never
/// anything the page sent; a frame whose origin isn't the top page's gets nothing (<see cref="VaultModel.CanFill"/>).
/// A password is read, with one Hello prompt, only once the user has chosen a login, and goes only into the page whose
/// origin the picker was shown for.
/// </summary>
/// <param name="vault">The vault; null while it's off.</param>
/// <param name="pageOf">The page of a tab that is on screen; null for one that isn't, or has gone.</param>
/// <param name="changed">The picker opened, changed or closed (null).</param>
/// <param name="focusPicker">Moves the keyboard into the picker.</param>
public sealed class Autofill(Func<VaultStore?> vault, Func<string, IPage?> pageOf, Action<AccountPicker?> changed, Action focusPicker)
{
	private sealed record Open(AccountPicker Picker, IPage Page, string Origin);

	private Open? _open;
	/// <summary>The last field each page reported, so the down arrow can bring a closed picker back.</summary>
	private readonly Dictionary<string, (Uri FrameSource, CredentialField Field)> _fields = new(StringComparer.Ordinal);

	public AccountPicker? Picker => _open?.Picker;

	/// <summary>A message from a page's script; <paramref name="frameSource"/> is WebView2's Source for it.</summary>
	public void Message(string pageId, Uri frameSource, AutofillMessage message)
	{
		switch (message)
		{
			case AutofillMessage.Focused focused:
				FieldFocused(pageId, frameSource, focused.Field);
				return;
			case AutofillMessage.Left:
				_fields.Remove(pageId);
				CloseFor(pageId);
				return;
			case AutofillMessage.Key { Pressed: PickerKey.Escape }:
				CloseFor(pageId);
				return;
			case AutofillMessage.Key { Pressed: PickerKey.Down }:
				Down(pageId);
				return;
		}
	}

	/// <summary>Shows the picker for the logins of the frame's actual origin, or closes it when there are none to offer.</summary>
	public void FieldFocused(string pageId, Uri frameSource, CredentialField field)
	{
		_fields[pageId] = (frameSource, field);
		Show(pageId, frameSource, field);
	}

	/// <summary>The tabs changed: a picker whose tab has left the screen goes.</summary>
	public void PagesChanged()
	{
		if (_open is { } open && !ReferenceEquals(pageOf(open.Picker.PageId), open.Page)) Close();
	}

	/// <summary>Esc in the picker: it closes and the page gets the keyboard back.</summary>
	public void Dismiss()
	{
		if (_open is not { } open) return;
		Close();
		open.Page.Focus();
	}

	/// <exception cref="AutofillEntryException">The picker on screen doesn't offer <paramref name="id"/>.</exception>
	public async Task<string> FillAsync(string pageId, string id)
	{
		// A choice that arrives after the picker closed (the page moved on meanwhile) has nothing to fill.
		if (_open is not { } open || open.Picker.PageId != pageId) return FillOutcomes.Refused;
		if (!open.Picker.Entries.Any(e => e.Id == id)) throw new AutofillEntryException(id);
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
		// The page may have gone elsewhere while Hello was up.
		if (!StillOn(open)) return FillOutcomes.Refused;
		var filled = await open.Page.EvaluateAsync(AutofillScripts.Fill(login.Username, password));
		return filled.ValueKind == JsonValueKind.True ? FillOutcomes.Filled : FillOutcomes.NotFilled;
	}

	private void Show(string pageId, Uri frameSource, CredentialField field)
	{
		if (vault() is not { } store || pageOf(pageId) is not { } page || !IsWeb(frameSource) || !Uri.TryCreate(page.Url, UriKind.Absolute, out var top) || !IsWeb(top))
		{
			CloseFor(pageId);
			return;
		}
		var context = new FillContext(top.AbsoluteUri, frameSource.AbsoluteUri);
		var entries = VaultModel.EntriesFor(store.Summaries(), context);
		if (entries.Count == 0)
		{
			CloseFor(pageId);
			return;
		}
		_open = new Open(new AccountPicker(pageId, field.Rect, [.. entries.Select(e => new PickerEntry(e.Id, e.Username))]), page, VaultModel.OriginOf(frameSource.AbsoluteUri));
		changed(_open.Picker);
	}

	private void Down(string pageId)
	{
		if (_open?.Picker.PageId != pageId && _fields.TryGetValue(pageId, out var last)) Show(pageId, last.FrameSource, last.Field);
		if (_open?.Picker.PageId == pageId) focusPicker();
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

	private void Close()
	{
		if (_open is null) return;
		_open = null;
		changed(null);
	}
}
