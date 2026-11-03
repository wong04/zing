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
	TimeSpan? attemptTimeout = null,
	TimeSpan? deadline = null)
{
	/// <summary>Generous on purpose: a degraded origin behind a CDN can take 20 s to answer, and cutting it off reports a slow server as a blocked one.</summary>
	public static readonly TimeSpan DefaultAttemptTimeout = TimeSpan.FromSeconds(35);

	/// <summary>The bridge cannot cancel a call, so a run stops itself. Room for the worst case: two slow attempts and a two-step archive.</summary>
	public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(180);

	private const string UserAgent = "Zing";
	private const string RelayBase = "https://r.jina.ai/";
	private const string HtmlAccept = "text/html,application/xhtml+xml,*/*";

	private readonly TimeSpan _attemptTimeout = attemptTimeout ?? DefaultAttemptTimeout;
	private readonly TimeSpan _deadline = deadline ?? DefaultDeadline;

	private sealed record Fetched(byte[] Body, string? ContentType, bool IsHtml, Uri FinalUrl);

	public async Task<RelayResult> OpenAsync(string input, RelayConsent consent, CancellationToken ct)
	{
		var url = RelayRules.Normalize(input);
		using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
		overall.CancelAfter(_deadline);
		try
		{
			return await TryEachAsync(url, consent, overall.Token);
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested)
		{
			throw new RelayTimedOutError();
		}
	}

	private async Task<RelayResult> TryEachAsync(Uri url, RelayConsent consent, CancellationToken run)
	{
		var attempts = new List<RelayAttempt>();
		void Report(RelayAttempt attempt)
		{
			attempts.Add(attempt);
			onAttempt?.Invoke(attempt);
		}

		var skipped = false;
		foreach (var strategy in RelayStrategies.InOrder)
		{
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
		using var attempt = CancellationTokenSource.CreateLinkedTokenSource(run);
		attempt.CancelAfter(_attemptTimeout);
		return strategy switch
		{
			RelayStrategy.Direct => await FetchPageAsync(url, attempt.Token),
			RelayStrategy.DohPinned => await FetchPinnedPageAsync(url, attempt.Token),
			RelayStrategy.ReaderRelay => await FetchReaderRelayAsync(url, attempt.Token),
			RelayStrategy.Wayback => await FetchWaybackAsync(url, attempt.Token),
			_ => throw new ArgumentOutOfRangeException(nameof(strategy)),
		};
	}

	/// <summary>No cookie, no credentials, and no pretending to be a browser: what an anonymous visitor named Zing would send.</summary>
	private static RelayRequest Request(Uri url, string accept) =>
		new(url, new Dictionary<string, string> { ["User-Agent"] = UserAgent, ["Accept"] = accept });

	private async Task<Fetched> FetchPageAsync(Uri url, CancellationToken ct)
	{
		var response = await transport.GetAsync(Request(url, HtmlAccept), ct);
		RelayRules.ValidateStatus(response.Status);
		return Page(response, isHtml: true);
	}

	private async Task<Fetched> FetchPinnedPageAsync(Uri url, CancellationToken ct)
	{
		var response = await transport.GetPinnedAsync(Request(url, HtmlAccept), dohProvider, ct);
		RelayRules.ValidateStatus(response.Status);
		return Page(response, isHtml: true);
	}

	// Jina's reader takes the address as a path suffix and answers with cleaned text.
	private async Task<Fetched> FetchReaderRelayAsync(Uri url, CancellationToken ct)
	{
		var response = await transport.GetAsync(Request(new Uri(RelayBase + url.AbsoluteUri), "text/plain"), ct);
		RelayRules.ValidateStatus(response.Status);
		return Page(response, isHtml: false) with { ContentType = "text/plain; charset=utf-8", FinalUrl = url };
	}

	private async Task<Fetched> FetchWaybackAsync(Uri url, CancellationToken ct)
	{
		var lookup = await transport.GetAsync(Request(new Uri($"https://archive.org/wayback/available?url={Uri.EscapeDataString(url.AbsoluteUri)}"), "application/json"), ct);
		RelayRules.ValidateStatus(lookup.Status);
		var snapshotUrl = RelayRules.WaybackSnapshot(ParseAnswer(lookup.Body));
		var snapshot = await transport.GetAsync(Request(snapshotUrl, HtmlAccept), ct);
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
