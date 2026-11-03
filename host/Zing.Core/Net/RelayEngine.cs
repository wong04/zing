using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zing.Core.Net;

/// <summary>How a page was reached, in the order they are tried: the fidelity falls and the number of parties who see the address rises.</summary>
[JsonConverter(typeof(RelayStrategyConverter))]
public enum RelayStrategy
{
	Direct,
	DohPinned,
	ReaderRelay,
	Wayback,
}

public sealed class RelayStrategyConverter() : JsonStringEnumConverter<RelayStrategy>(JsonNamingPolicy.CamelCase);

public static class RelayStrategies
{
	public static readonly IReadOnlyList<RelayStrategy> InOrder = [RelayStrategy.Direct, RelayStrategy.DohPinned, RelayStrategy.ReaderRelay, RelayStrategy.Wayback];

	public static string Label(RelayStrategy strategy) => strategy switch
	{
		RelayStrategy.Direct => "Direct",
		RelayStrategy.DohPinned => "DoH + IP pinning",
		RelayStrategy.ReaderRelay => "Reader relay",
		RelayStrategy.Wayback => "Wayback archive",
		_ => throw new ArgumentOutOfRangeException(nameof(strategy)),
	};

	/// <summary>Whether another party than the site sees the address, and so needs the user's yes first.</summary>
	public static bool NeedsConsent(RelayStrategy strategy) => strategy is RelayStrategy.ReaderRelay or RelayStrategy.Wayback;
}

/// <summary>Whether the reader relay and the Wayback archive may be asked this session. Kept in memory only: it is never written to settings.</summary>
public sealed class RelayConsent
{
	private volatile bool _allowed;

	public bool ThirdPartiesAllowed => _allowed;

	public void Allow() => _allowed = true;
}

[JsonConverter(typeof(AttemptStatusConverter))]
public enum AttemptStatus
{
	Trying,
	Failed,
	Worked,
	/// <summary>Not tried, because it needs the user's yes first.</summary>
	Skipped,
}

public sealed class AttemptStatusConverter() : JsonStringEnumConverter<AttemptStatus>(JsonNamingPolicy.CamelCase);

/// <summary>How long one request may take. Each request has its own, so the archive's lookup and its snapshot do not share one.</summary>
/// <param name="Page">A page fetched direct or pinned, and the archive's lookup. Generous on purpose: a degraded origin behind a CDN can take 20 s to answer, and cutting it off reports a slow server as a blocked one.</param>
/// <param name="Relay">The reader relay, which renders the page, and the archive's snapshot, which comes from cold storage.</param>
public sealed record RelayTimeouts(TimeSpan Page, TimeSpan Relay)
{
	public static RelayTimeouts Default { get; } = new(TimeSpan.FromSeconds(35), TimeSpan.FromSeconds(30));
}

/// <param name="Reason">Why it failed or was skipped.</param>
public sealed record RelayAttempt(RelayStrategy Strategy, AttemptStatus Status, string? Reason = null);

[JsonConverter(typeof(RelayOutcomeConverter))]
public enum RelayOutcome
{
	Opened,
	/// <summary>The site could not be reached directly, and the next methods are third parties the user hasn't agreed to yet.</summary>
	NeedsConsent,
}

public sealed class RelayOutcomeConverter() : JsonStringEnumConverter<RelayOutcome>(JsonNamingPolicy.CamelCase);

/// <param name="Content">The page as text; lossy for a page that isn't UTF-8, for which <see cref="Body"/> has the bytes.</param>
public sealed record RelayResult(
	RelayOutcome Outcome,
	RelayStrategy? Strategy,
	string Content,
	string? ContentType,
	bool IsHtml,
	string? FinalUrl,
	IReadOnlyList<RelayAttempt> Attempts)
{
	/// <summary>The bytes as the server sent them. Not sent over the bridge.</summary>
	[JsonIgnore]
	public byte[] Body { get; init; } = [];
}

/// <summary>
/// Tries each way of reaching a page in order and reports every attempt as it happens (port of src/platform/net/relayFetch.ts).
/// The last two are third parties, so they wait for <see cref="RelayConsent"/>.
/// </summary>
public sealed class RelayEngine(
	IRelayTransport transport,
	string dohProvider,
	Action<RelayAttempt>? onAttempt = null,
	RelayTimeouts? timeouts = null,
	TimeSpan? deadline = null)
{
	/// <summary>The bridge cannot cancel a call, so a run stops itself. Room for the worst case: two slow attempts and a two-step archive.</summary>
	public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(180);

	private const string UserAgent = "Zing";
	private const string RelayBase = "https://r.jina.ai/";
	private const string HtmlAccept = "text/html,application/xhtml+xml,*/*";

	private readonly RelayTimeouts _timeouts = timeouts ?? RelayTimeouts.Default;
	private readonly TimeSpan _deadline = deadline ?? DefaultDeadline;

	private sealed record Fetched(byte[] Body, string? ContentType, bool IsHtml, Uri FinalUrl);

	public Task<RelayResult> OpenAsync(string input, RelayConsent consent, CancellationToken ct) =>
		RunAsync(RelayRules.Normalize(input), consent, RelayStrategies.InOrder, ct);

	/// <summary>
	/// A file a relayed page asks for (style, script, image). The reader relay is left out: it answers with a text rendering,
	/// not the file. Consent and the own-network rule apply as for a page.
	/// </summary>
	public Task<RelayResult> OpenResourceAsync(Uri url, RelayConsent consent, CancellationToken ct) =>
		RunAsync(RelayRules.Normalize(url.AbsoluteUri), consent, ResourceStrategies, ct);

	private static readonly IReadOnlyList<RelayStrategy> ResourceStrategies = [.. RelayStrategies.InOrder.Where(s => s != RelayStrategy.ReaderRelay)];

	private async Task<RelayResult> RunAsync(Uri url, RelayConsent consent, IReadOnlyList<RelayStrategy> strategies, CancellationToken ct)
	{
		using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
		overall.CancelAfter(_deadline);
		try
		{
			return await TryEachAsync(url, consent, strategies, overall.Token);
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested)
		{
			throw new RelayTimedOutError();
		}
	}

	private async Task<RelayResult> TryEachAsync(Uri url, RelayConsent consent, IReadOnlyList<RelayStrategy> strategies, CancellationToken run)
	{
		var attempts = new List<RelayAttempt>();
		void Report(RelayAttempt attempt)
		{
			attempts.Add(attempt);
			onAttempt?.Invoke(attempt);
		}

		var ownNetwork = RelayRules.IsPrivateHost(url);
		var skipped = false;
		foreach (var strategy in strategies)
		{
			// Checked first: a yes would not help, because such an address is never sent.
			if (RelayStrategies.NeedsConsent(strategy) && ownNetwork)
			{
				Report(new RelayAttempt(strategy, AttemptStatus.Skipped, "That address is on your own network, so Zing doesn't send it to a third party."));
				continue;
			}
			if (RelayStrategies.NeedsConsent(strategy) && !consent.ThirdPartiesAllowed)
			{
				Report(new RelayAttempt(strategy, AttemptStatus.Skipped, "Needs your OK to send the address to a third party."));
				skipped = true;
				continue;
			}
			Report(new RelayAttempt(strategy, AttemptStatus.Trying));
			try
			{
				var fetched = await FetchAsync(strategy, url, run);
				Report(new RelayAttempt(strategy, AttemptStatus.Worked));
				return Opened(strategy, fetched, attempts);
			}
			catch (Exception e) when (IsAttemptFailure(e, run))
			{
				Report(new RelayAttempt(strategy, AttemptStatus.Failed, ReasonOf(e)));
			}
		}
		if (skipped) return new RelayResult(RelayOutcome.NeedsConsent, null, "", null, false, null, attempts);
		throw new AllStrategiesFailedError(attempts);
	}

	/// <summary>A cancel or the run's deadline ends everything; only a problem with this one method moves on to the next.</summary>
	private static bool IsAttemptFailure(Exception e, CancellationToken run) =>
		e is HttpRequestException or AuthenticationException or NetError or IOException
		|| (e is OperationCanceledException && !run.IsCancellationRequested);

	private static string ReasonOf(Exception e) => e switch
	{
		OperationCanceledException => "Didn't answer in time.",
		HttpRequestException { InnerException: { } inner } => inner.Message,
		_ => e.Message,
	};

	private static RelayResult Opened(RelayStrategy strategy, Fetched fetched, List<RelayAttempt> attempts) =>
		new(RelayOutcome.Opened, strategy, Encoding.UTF8.GetString(fetched.Body), fetched.ContentType, fetched.IsHtml, fetched.FinalUrl.AbsoluteUri, attempts)
		{
			Body = fetched.Body,
		};

	private async Task<Fetched> FetchAsync(RelayStrategy strategy, Uri url, CancellationToken run)
	{
		return strategy switch
		{
			RelayStrategy.Direct => await FetchPageAsync(url, run),
			RelayStrategy.DohPinned => await FetchPinnedPageAsync(url, run),
			RelayStrategy.ReaderRelay => await FetchReaderRelayAsync(url, run),
			RelayStrategy.Wayback => await FetchWaybackAsync(url, run),
			_ => throw new ArgumentOutOfRangeException(nameof(strategy)),
		};
	}

	private static async Task<RelayResponse> WithinAsync(TimeSpan limit, CancellationToken run, Func<CancellationToken, Task<RelayResponse>> request)
	{
		using var step = CancellationTokenSource.CreateLinkedTokenSource(run);
		step.CancelAfter(limit);
		return await request(step.Token);
	}

	/// <summary>No cookie, no credentials, and no pretending to be a browser: what an anonymous visitor named Zing would send.</summary>
	private static RelayRequest Request(Uri url, string accept) =>
		new(url, new Dictionary<string, string> { ["User-Agent"] = UserAgent, ["Accept"] = accept });

	private async Task<Fetched> FetchPageAsync(Uri url, CancellationToken ct)
	{
		var response = await WithinAsync(_timeouts.Page, ct, token => transport.GetAsync(Request(url, HtmlAccept), token));
		RelayRules.ValidateStatus(response.Status);
		return Page(response, isHtml: true);
	}

	private async Task<Fetched> FetchPinnedPageAsync(Uri url, CancellationToken ct)
	{
		var response = await WithinAsync(_timeouts.Page, ct, token => transport.GetPinnedAsync(Request(url, HtmlAccept), dohProvider, token));
		RelayRules.ValidateStatus(response.Status);
		return Page(response, isHtml: true);
	}

	// Jina's reader takes the address as a path suffix and answers with cleaned text.
	private async Task<Fetched> FetchReaderRelayAsync(Uri url, CancellationToken ct)
	{
		var response = await WithinAsync(_timeouts.Relay, ct, token => transport.GetAsync(Request(new Uri(RelayBase + url.AbsoluteUri), "text/plain"), token));
		RelayRules.ValidateStatus(response.Status);
		return Page(response, isHtml: false) with { ContentType = "text/plain; charset=utf-8", FinalUrl = url };
	}

	private async Task<Fetched> FetchWaybackAsync(Uri url, CancellationToken ct)
	{
		var lookup = await WithinAsync(_timeouts.Page, ct, token => transport.GetAsync(Request(new Uri($"https://archive.org/wayback/available?url={Uri.EscapeDataString(url.AbsoluteUri)}"), "application/json"), token));
		RelayRules.ValidateStatus(lookup.Status);
		var snapshotUrl = RelayRules.WaybackSnapshot(ParseAnswer(lookup.Body));
		var snapshot = await WithinAsync(_timeouts.Relay, ct, token => transport.GetAsync(Request(snapshotUrl, HtmlAccept), token));
		RelayRules.ValidateStatus(snapshot.Status);
		return Page(snapshot, isHtml: true) with { FinalUrl = snapshotUrl };
	}

	private static JsonElement ParseAnswer(byte[] body)
	{
		try
		{
			using var document = JsonDocument.Parse(body);
			return document.RootElement.Clone();
		}
		catch (JsonException)
		{
			throw new MalformedRelayAnswerError();
		}
	}

	private static Fetched Page(RelayResponse response, bool isHtml) =>
		response.Body.Length == 0 ? throw new EmptyBodyError() : new Fetched(response.Body, response.ContentType, isHtml, response.FinalUrl);
}

/// <summary>What <c>relay:attempt</c> pushes. The run id is the one the caller gave <c>relay:open</c>, so overlapping runs stay apart.</summary>
public sealed record RelayAttemptEvent(string RunId, RelayStrategy Strategy, AttemptStatus Status, string? Reason);
