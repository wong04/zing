using System.Security.Cryptography;
using System.Text;

namespace Zing.Core.Vault;

public enum SignInOutcome { Success, Failed, Pending }

/// <summary>
/// Whether a sign-in worked. The C# twin of src/shared/vault/signInOutcome.ts, held to it by signInOutcome.vectors.json:
/// the page landed on in the origin the form was submitted from has no visible password box (success), or has one again
/// (failed); a landing in another origin says nothing (pending).
/// </summary>
public static class SignInOutcomes
{
	public static SignInOutcome Of(string submittedOrigin, string landedOrigin, bool hasVisiblePasswordBox)
	{
		if (landedOrigin != submittedOrigin) return SignInOutcome.Pending;
		return hasVisiblePasswordBox ? SignInOutcome.Failed : SignInOutcome.Success;
	}

	/// <summary>Ruling 37: the sign-in form came back in the origin a prompt is open for.</summary>
	public static bool PromptWithdrawn(string promptOrigin, string landedOrigin, bool hasVisiblePasswordBox) =>
		landedOrigin == promptOrigin && hasVisiblePasswordBox;

	/// <summary>
	/// Whether a username entered at one origin may be paired with a password entered at the other, as Chrome's
	/// IsPublicSuffixDomainMatch decides: the same scheme and port, and the same registrable domain by the full Public Suffix List.
	/// </summary>
	public static bool SameSite(string a, string b)
	{
		if (!Uri.TryCreate(a, UriKind.Absolute, out var x) || !Uri.TryCreate(b, UriKind.Absolute, out var y)) return false;
		if (x.Scheme != y.Scheme || x.Port != y.Port) return false;
		// localhost, an intranet name or a host that is itself a suffix has no registrable domain, but is its own site.
		if (x.IdnHost == y.IdnHost) return true;
		return PublicSuffix.RegistrableDomain(x.IdnHost) is { } site && site == PublicSuffix.RegistrableDomain(y.IdnHost);
	}
}

/// <summary>Pushed in the page-UI state. Names the origin and the username only: the password never leaves the host.</summary>
/// <param name="Kind"><c>save</c> or <c>update</c>.</param>
/// <param name="Replaces">For an update, the saved login's username it replaces the password of.</param>
public sealed record SaveLoginPrompt(string PageId, string Kind, string Origin, string Username, string? Replaces);

public static class SaveAnswers
{
	public const string Save = "save";
	public const string NotNow = "not-now";
	public const string Never = "never";
}

public static class SaveOutcomes
{
	/// <summary>An update whose password turned out to be the one already saved: nothing was stored.</summary>
	public const string Unchanged = "unchanged";
	public const string Saved = "saved";
	public const string Updated = "updated";
	/// <summary>Not now, or never for this site.</summary>
	public const string Dismissed = "dismissed";
	/// <summary>Windows Hello was cancelled or is unavailable: nothing was stored, and it isn't an error.</summary>
	public const string Cancelled = "cancelled";
	/// <summary>The prompt was already answered or had gone (its tab closed, the time ran out).</summary>
	public const string Gone = "gone";
}

/// <summary>vault:answer-save was given something other than save, not-now or never.</summary>
public sealed class SaveAnswerException(string answer) : ArgumentException($"Not an answer to the save prompt: {answer}");

/// <summary>
/// One window's save and update prompts. A page reports a submit and, later, where it landed; this holds the credentials
/// in memory only, between the two, and turns a successful sign-in into a prompt (SaveDecision decides which, or none).
/// The origin is always the frame's actual source as WebView2 reports it, so a page can prompt only for itself and
/// with what it reported (Ruling 33). Credentials are never logged, persisted or sent to the renderer; they are dropped
/// after <see cref="HoldFor"/> without a landing, once the prompt is answered or its time runs out, when the tab closes,
/// and when the vault turns off. For a two-step sign-in it also remembers each tab's latest username step, in memory, for
/// <see cref="UsernameStepFor"/>, and pairs it with a password submitted on the same site with no username of its own.
/// </summary>
/// <param name="vault">The vault; null while it's off.</param>
/// <param name="isShown">Whether a tab is on screen; a prompt is for the tab the user is looking at.</param>
/// <param name="changed">The prompt on screen changed, or closed (null).</param>
/// <param name="background">Runs a decision nobody awaits, reporting its failure.</param>
public sealed class SignInWatch(Func<VaultStore?> vault, Func<string, bool> isShown, IScheduler scheduler, Action<SaveLoginPrompt?> changed, Action<Task, string> background) : IDisposable
{
	/// <summary>A submit waits this long for the page to land (spec: after redirects, within 10 seconds).</summary>
	public static readonly TimeSpan HoldFor = TimeSpan.FromMilliseconds(10_000);

	/// <summary>A prompt nobody answers closes after this long, and its password goes with it.</summary>
	public static readonly TimeSpan PromptFor = TimeSpan.FromMinutes(2);

	/// <summary>
	/// What a fill by Zing is remembered for, so signing in with it isn't offered as an update. Still needed with the secret key
	/// held for 5 minutes (Ruling 44): a landing never reads a password (Ruling 36), held key or not, so without it every fill
	/// followed by a sign-in would prompt an update.
	/// </summary>
	public static readonly TimeSpan FillFor = TimeSpan.FromMinutes(2);

	/// <summary>How long a two-step sign-in's username is remembered: Chrome's kSingleUsernameTimeToLive.</summary>
	public static readonly TimeSpan UsernameStepFor = TimeSpan.FromMinutes(5);

	/// <summary>Ruling 33; signInLimits in signInOutcome.ts.</summary>
	public const int UsernameMax = 512;
	public const int PasswordMax = 1024;

	private sealed class Held(string origin, string username, string password, bool fromVault)
	{
		public string Origin { get; } = origin;
		public string Username { get; } = username;
		public string Password { get; } = password;
		/// <summary>The password is the one Zing itself filled for this login, so it matches the saved one.</summary>
		public bool FromVault { get; } = fromVault;
		public IDisposable? Timer { get; set; }
		/// <summary>The page landed: this submit has had its one prompt.</summary>
		public bool Landed { get; set; }

	}

	/// <summary>A class, not a record: a record's ToString would print the password.</summary>
	private sealed class Shown(SaveLoginPrompt prompt, string password, string? savedId, IDisposable timer)
	{
		public SaveLoginPrompt Prompt { get; } = prompt;
		public string Password { get; } = password;
		public string? SavedId { get; } = savedId;
		public IDisposable Timer { get; } = timer;
	}

	private sealed record Fill(string Origin, string Username, byte[] Digest, object Token, IDisposable Timer);

	private sealed record Step(string Origin, string Username, IDisposable Timer);

	private readonly Dictionary<string, Held> _held = new(StringComparer.Ordinal);
	private readonly Dictionary<string, Fill> _fills = new(StringComparer.Ordinal);
	private readonly Dictionary<string, Step> _steps = new(StringComparer.Ordinal);
	private readonly byte[] _digestKey = RandomNumberGenerator.GetBytes(32);
	private Shown? _shown;

	public SaveLoginPrompt? Prompt => _shown?.Prompt;

	/// <summary>How many pages have credentials held, waiting for a landing or a decision.</summary>
	public int HeldCount => _held.Count;

	/// <summary>A message from a page's script; <paramref name="frameSource"/> is WebView2's Source for it.</summary>
	public void Message(string pageId, Uri frameSource, AutofillMessage message)
	{
		if (message is not (AutofillMessage.Submitted or AutofillMessage.UsernameStep or AutofillMessage.Landed or AutofillMessage.Left)) return;
		if (frameSource.Scheme != Uri.UriSchemeHttps && frameSource.Scheme != Uri.UriSchemeHttp) return;
		var origin = VaultModel.OriginOf(frameSource.AbsoluteUri);
		switch (message)
		{
			case AutofillMessage.Left:
				// The page is going to another origin: a prompt for the old one no longer fits over it.
				if (_shown is { } open && open.Prompt.PageId == pageId && open.Prompt.Origin != origin) CloseShown();
				return;
			case AutofillMessage.Submitted submitted:
				Submitted(pageId, origin, submitted.Username, submitted.Password, submitted.HiddenUsername);
				return;
			case AutofillMessage.UsernameStep step:
				UsernameStep(pageId, origin, step.Username);
				return;
			case AutofillMessage.Landed landed:
				Landed(pageId, origin, landed.HasVisiblePasswordBox);
				return;
		}
	}

	/// <summary>
	/// A form was submitted. The latest submit of a page replaces the one held, and a prompt for the old one closes. With no
	/// username of its own it takes the page's username step on the same site, else the page's hidden username field, else
	/// nothing is held: a password alone is never offered.
	/// </summary>
	public void Submitted(string pageId, string origin, string username, string password, string? hiddenUsername = null)
	{
		if (vault() is not { } store) return;
		if (username.Length == 0)
			username = (_steps.TryGetValue(pageId, out var step) && SignInOutcomes.SameSite(step.Origin, origin) ? step.Username : hiddenUsername) ?? "";
		// A password alone (a re-auth box, a code page) isn't a sign-in to offer, and leaves an open prompt be.
		if (username.Length == 0) return;
		Drop(pageId);
		// Nothing will be offered for it, so don't hold its password at all.
		if (store.IsNeverSave(origin)) return;
		var held = new Held(origin, username, password, FilledByZing(pageId, origin, username, password));
		held.Timer = scheduler.Schedule(HoldFor, () => Expire(pageId, held));
		_held[pageId] = held;
	}

	/// <summary>
	/// The page landed (a new document loaded, or a single-page sign-in swapped its form). A success turns the held sign-in
	/// into at most one prompt; a failure drops it; a landing somewhere else waits for the next.
	/// </summary>
	public void Landed(string pageId, string origin, bool hasVisiblePasswordBox)
	{
		if (!_held.TryGetValue(pageId, out var held))
		{
			Withdraw(pageId, origin, hasVisiblePasswordBox);
			return;
		}
		if (held.Landed) return;
		switch (SignInOutcomes.Of(held.Origin, origin, hasVisiblePasswordBox))
		{
			case SignInOutcome.Pending:
				return;
			case SignInOutcome.Failed:
				Drop(pageId);
				return;
		}
		held.Landed = true;
		held.Timer?.Dispose();
		// The sign-in worked, so the username step that may have led to it is done with (Chrome clears it too).
		ForgetStep(pageId);
		background(DecideAsync(pageId, held), "save prompt");
	}

	/// <summary>The first page of a two-step sign-in. The latest step of a page replaces the one remembered.</summary>
	public void UsernameStep(string pageId, string origin, string username)
	{
		if (vault() is not { } store) return;
		ForgetStep(pageId);
		if (store.IsNeverSave(origin)) return;
		Step? step = null;
		step = new Step(origin, username, scheduler.Schedule(UsernameStepFor, () =>
		{
			if (_steps.TryGetValue(pageId, out var current) && ReferenceEquals(current, step)) ForgetStep(pageId);
		}));
		_steps[pageId] = step;
	}

	/// <summary>The user sent the tab somewhere themselves (the address bar, a bookmark, Home): a username step is left behind.</summary>
	public void UserNavigated(string pageId) => ForgetStep(pageId);

	private void ForgetStep(string pageId)
	{
		if (_steps.Remove(pageId, out var step)) step.Timer.Dispose();
	}

	/// <summary>Ruling 37: the sign-in page is back in the origin a prompt is open for, so that sign-in failed after all.</summary>
	private void Withdraw(string pageId, string origin, bool hasVisiblePasswordBox)
	{
		if (_shown is { } open && open.Prompt.PageId == pageId && SignInOutcomes.PromptWithdrawn(open.Prompt.Origin, origin, hasVisiblePasswordBox)) CloseShown();
	}

	/// <summary>Zing filled this login into the page, so signing in with it again isn't a changed password.</summary>
	public void Filled(string pageId, string origin, string username, string password)
	{
		if (_fills.Remove(pageId, out var old)) Forget(old);
		var token = new object();
		_fills[pageId] = new Fill(origin, username, Digest(password), token, scheduler.Schedule(FillFor, () =>
		{
			if (_fills.TryGetValue(pageId, out var current) && ReferenceEquals(current.Token, token))
			{
				_fills.Remove(pageId);
				Forget(current);
			}
		}));
	}

	private bool FilledByZing(string pageId, string origin, string username, string password) =>
		_fills.TryGetValue(pageId, out var fill) && fill.Origin == origin && fill.Username == username && CryptographicOperations.FixedTimeEquals(fill.Digest, Digest(password));

	private byte[] Digest(string password) => HMACSHA256.HashData(_digestKey, Encoding.UTF8.GetBytes(password));

	/// <summary>
	/// A landing never asks Hello (Ruling 36, a page could otherwise make it pop up at will). A saved login gets an update
	/// prompt unless Zing filled that password itself; whether it really changed is checked inside the one Hello of the click.
	/// Whatever goes wrong, what is held is dropped unless it moved into a prompt.
	/// </summary>
	private Task DecideAsync(string pageId, Held held)
	{
		try
		{
			Decide(pageId, held);
			return Task.CompletedTask;
		}
		catch (Exception e)
		{
			return Task.FromException(e);
		}
		finally
		{
			Drop(pageId, held);
		}
	}

	private void Decide(string pageId, Held held)
	{
		if (vault() is not { } store) return;
		var summaries = store.Summaries();
		// passwordChanged is true here so a saved login comes out as Update; whether it really changed is checked on the click.
		var choice = VaultModel.SaveDecision(summaries, held.Origin, held.Username, passwordChanged: true, store.NeverSaves());
		if (choice == SaveChoice.None || !isShown(pageId)) return;
		string? savedId = null;
		if (choice == SaveChoice.Update)
		{
			if (held.FromVault) return;
			savedId = summaries.First(s => s.DeletedAt is null && s.Origin == held.Origin && s.Username == held.Username).Id;
		}
		_held.Remove(pageId);
		held.Timer?.Dispose();
		Show(new SaveLoginPrompt(pageId, choice == SaveChoice.Update ? "update" : "save", held.Origin, held.Username, choice == SaveChoice.Update ? held.Username : null), held.Password, savedId);
	}

	private void Show(SaveLoginPrompt prompt, string password, string? savedId)
	{
		CloseShown();
		var timer = scheduler.Schedule(PromptFor, () =>
		{
			if (_shown is { } shown && shown.Prompt == prompt) CloseShown();
		});
		_shown = new Shown(prompt, password, savedId, timer);
		changed(prompt);
	}

	/// <summary>The user's answer to the prompt on screen (vault:answer-save). Saving and updating ask Hello; a cancelled Hello stores nothing.</summary>
	/// <exception cref="SaveAnswerException">The answer isn't one of save, not-now and never.</exception>
	public async Task<string> AnswerAsync(string pageId, string answer)
	{
		if (answer is not (SaveAnswers.Save or SaveAnswers.NotNow or SaveAnswers.Never)) throw new SaveAnswerException(answer);
		if (_shown is not { } shown || shown.Prompt.PageId != pageId) return SaveOutcomes.Gone;
		// The prompt closes first: Hello may be up for a while, and a second click must not save twice.
		CloseShown();
		var prompt = shown.Prompt;
		if (answer == SaveAnswers.NotNow) return SaveOutcomes.Dismissed;
		if (vault() is not { } store) return SaveOutcomes.Gone;
		try
		{
			if (answer == SaveAnswers.Never)
			{
				await store.NeverSave(prompt.Origin);
				return SaveOutcomes.Dismissed;
			}
			if (prompt.Kind == "update")
			{
				var saved = store.Summaries().FirstOrDefault(s => s.Id == shown.SavedId && s.DeletedAt is null) ?? throw new VaultItemNotFoundException(shown.SavedId ?? "");
				return await store.UpdateLoginIfChangedAsync(saved.Id, saved.Title, saved.Note, saved.Username, shown.Password) ? SaveOutcomes.Updated : SaveOutcomes.Unchanged;
			}
			// Another window, or a sync, may have saved this login while the prompt was open.
			if (store.Summaries().Any(s => s.DeletedAt is null && s.Origin == prompt.Origin && s.Username == prompt.Username)) return SaveOutcomes.Gone;
			await store.SaveAsync(new NewLogin(prompt.Origin, prompt.Username, shown.Password, new Uri(prompt.Origin).Host, ""));
			return SaveOutcomes.Saved;
		}
		catch (Exception e) when (e is HelloCancelledException or HelloUnavailableException)
		{
			return SaveOutcomes.Cancelled;
		}
		catch (VaultClosedException)
		{
			// The vault was turned off while Hello was up: nothing was stored, and there's nothing to tell the user.
			return SaveOutcomes.Gone;
		}
	}

	/// <summary>The tab closed.</summary>
	public void PageClosed(string pageId)
	{
		Drop(pageId);
		ForgetStep(pageId);
		if (_fills.Remove(pageId, out var fill)) Forget(fill);
	}

	/// <summary>The tabs changed: a prompt whose tab is no longer on screen goes, and its password with it.</summary>
	public void PagesChanged()
	{
		if (_shown is { } shown && !isShown(shown.Prompt.PageId)) CloseShown();
	}

	/// <summary>Everything held goes: the vault turned off.</summary>
	public void Clear()
	{
		ForgetHeld();
		CloseShown();
	}

	private void ForgetHeld()
	{
		foreach (var held in _held.Values) held.Timer?.Dispose();
		_held.Clear();
		foreach (var fill in _fills.Values) Forget(fill);
		_fills.Clear();
		foreach (var step in _steps.Values) step.Timer.Dispose();
		_steps.Clear();
	}

	private void Expire(string pageId, Held held)
	{
		if (_held.TryGetValue(pageId, out var current) && ReferenceEquals(current, held) && !held.Landed) Drop(pageId);
	}

	private void Drop(string pageId, Held? only = null)
	{
		if (_held.TryGetValue(pageId, out var held) && (only is null || ReferenceEquals(held, only)))
		{
			held.Timer?.Dispose();
			_held.Remove(pageId);
		}
		if (_shown?.Prompt.PageId == pageId && only is null) CloseShown();
	}

	private void CloseShown()
	{
		if (_shown is not { } shown) return;
		_shown = null;
		shown.Timer.Dispose();
		changed(null);
	}

	private static void Forget(Fill fill)
	{
		fill.Timer.Dispose();
		CryptographicOperations.ZeroMemory(fill.Digest);
	}

	/// <summary>
	/// The window is closing, so nothing is pushed to its views: telling a closing view that the prompt went throws, and that
	/// once stopped ChromeApi.Dispose half way (Task 6 review I3). The key is zeroed whatever happens.
	/// </summary>
	public void Dispose()
	{
		try
		{
			ForgetHeld();
			if (_shown is { } shown)
			{
				_shown = null;
				shown.Timer.Dispose();
			}
		}
		finally
		{
			CryptographicOperations.ZeroMemory(_digestKey);
		}
	}
}
