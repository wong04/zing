using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using Zing.Core.Net;

namespace Zing.Core.Tests.Net;

public sealed class RelayEngineTests
{
	private const string VectorsPath = "src/shared/unblock/relayEngine.vectors.json";
	private const string Page = "<html>hello</html>";

	internal sealed class FakeTransport : IRelayTransport
	{
		public Func<RelayRequest, Task<RelayResponse>> Plain { get; set; } = request => throw new HttpRequestException($"no route to {request.Url.Host}");
		public Func<RelayRequest, Task<RelayResponse>> Pinned { get; set; } = request => throw new HttpRequestException($"no route to {request.Url.Host}");
		public List<(string Via, RelayRequest Request)> Calls { get; } = [];

		public Task<RelayResponse> GetAsync(RelayRequest request, CancellationToken ct)
		{
			Calls.Add(("plain", request));
			// A real transport stops when its token does; WaitAsync gives a script that never answers the same behaviour.
			return Plain(request).WaitAsync(ct);
		}

		public Task<RelayResponse> GetPinnedAsync(RelayRequest request, string dohProvider, CancellationToken ct)
		{
			Calls.Add(("pinned", request));
			return Pinned(request).WaitAsync(ct);
		}
	}

	internal static Task<RelayResponse> Ok(string body, string? type = "text/html", int status = 200, string url = "https://example.com/x") =>
		Task.FromResult(new RelayResponse(status, Encoding.UTF8.GetBytes(body), type, new Uri(url)));

	private static async Task<RelayResponse> Hangs(RelayRequest request)
	{
		await Task.Delay(Timeout.Infinite);
		throw new InvalidOperationException("unreachable");
	}

	private static bool IsRelay(RelayRequest r) => r.Url.Host == "r.jina.ai";
	private static bool IsArchive(RelayRequest r) => r.Url.Host == "archive.org" || r.Url.Host == "web.archive.org";

	private const string WaybackAnswer = """{"archived_snapshots":{"closest":{"available":true,"url":"http://web.archive.org/web/2024/http://example.com/x"}}}""";

	private static RelayConsent Allowed()
	{
		var consent = new RelayConsent();
		consent.Allow();
		return consent;
	}

	private static (RelayEngine Engine, List<RelayAttempt> Seen) EngineFor(FakeTransport transport, RelayTimeouts? timeouts = null, TimeSpan? deadline = null)
	{
		var seen = new List<RelayAttempt>();
		return (new RelayEngine(transport, "cloudflare", seen.Add, timeouts, deadline), seen);
	}

	private static string[] Describe(IEnumerable<RelayAttempt> attempts) => [.. attempts.Select(a => $"{a.Strategy}:{a.Status}")];

	[Fact]
	public async Task Direct_success_stops_there()
	{
		var transport = new FakeTransport { Plain = _ => Ok(Page) };
		var (engine, seen) = EngineFor(transport);
		var result = await engine.OpenAsync("example.com/x", new RelayConsent(), CancellationToken.None);
		Assert.Equal(RelayOutcome.Opened, result.Outcome);
		Assert.Equal(RelayStrategy.Direct, result.Strategy);
		Assert.Equal(Page, result.Content);
		Assert.True(result.IsHtml);
		Assert.Single(transport.Calls);
		Assert.Equal(["Direct:Trying", "Direct:Worked"], Describe(seen));
		Assert.Equal(Describe(seen), Describe(result.Attempts));
	}

	[Fact]
	public async Task Direct_failing_then_pinned_success_reports_both()
	{
		var transport = new FakeTransport { Pinned = _ => Ok(Page) };
		var (engine, seen) = EngineFor(transport);
		var result = await engine.OpenAsync("example.com/x", new RelayConsent(), CancellationToken.None);
		Assert.Equal(RelayStrategy.DohPinned, result.Strategy);
		Assert.Equal(["Direct:Trying", "Direct:Failed", "DohPinned:Trying", "DohPinned:Worked"], Describe(seen));
		Assert.Contains("no route to example.com", seen[1].Reason);
		Assert.Equal(["plain", "pinned"], transport.Calls.Select(c => c.Via));
	}

	[Fact]
	public async Task An_error_status_is_a_failed_attempt_and_the_engine_moves_on()
	{
		var transport = new FakeTransport { Plain = _ => Ok("nope", status: 404), Pinned = _ => Ok(Page) };
		var (engine, seen) = EngineFor(transport);
		var result = await engine.OpenAsync("example.com/x", new RelayConsent(), CancellationToken.None);
		Assert.Equal(RelayStrategy.DohPinned, result.Strategy);
		Assert.Equal("Server returned HTTP 404.", seen[1].Reason);
	}

	[Fact]
	public async Task An_empty_body_is_a_failed_attempt()
	{
		var transport = new FakeTransport { Plain = _ => Ok(""), Pinned = _ => Ok(Page) };
		var (engine, seen) = EngineFor(transport);
		await engine.OpenAsync("example.com/x", new RelayConsent(), CancellationToken.None);
		Assert.Equal("The response came back empty.", seen[1].Reason);
	}

	[Fact]
	public async Task A_certificate_refusal_on_the_pinned_attempt_is_reported_and_the_engine_moves_on()
	{
		var transport = new FakeTransport
		{
			Pinned = _ => throw new System.Security.Authentication.AuthenticationException("the certificate is not for this name"),
			Plain = request => IsRelay(request) ? Ok("text") : throw new HttpRequestException("blocked"),
		};
		var (engine, seen) = EngineFor(transport);
		var result = await engine.OpenAsync("example.com/x", Allowed(), CancellationToken.None);
		Assert.Equal(RelayStrategy.ReaderRelay, result.Strategy);
		Assert.Contains("certificate", seen[3].Reason);
	}

	[Fact]
	public async Task Without_consent_the_third_parties_are_not_called_and_the_result_says_consent_is_needed()
	{
		var transport = new FakeTransport();
		var (engine, seen) = EngineFor(transport);
		var result = await engine.OpenAsync("example.com/x", new RelayConsent(), CancellationToken.None);
		Assert.Equal(RelayOutcome.NeedsConsent, result.Outcome);
		Assert.Null(result.Strategy);
		Assert.DoesNotContain(transport.Calls, c => IsRelay(c.Request) || IsArchive(c.Request));
		Assert.Equal(["Direct:Trying", "Direct:Failed", "DohPinned:Trying", "DohPinned:Failed", "ReaderRelay:Skipped", "Wayback:Skipped"], Describe(seen));
		Assert.Equal(Describe(seen), Describe(result.Attempts));
	}

	[Fact]
	public async Task Without_consent_a_direct_success_never_asks_for_it()
	{
		var transport = new FakeTransport { Plain = _ => Ok(Page) };
		var (engine, _) = EngineFor(transport);
		var result = await engine.OpenAsync("example.com/x", new RelayConsent(), CancellationToken.None);
		Assert.Equal(RelayOutcome.Opened, result.Outcome);
	}

	[Fact]
	public async Task With_consent_the_reader_relay_is_asked_for_the_address_as_a_path()
	{
		var transport = new FakeTransport { Plain = request => IsRelay(request) ? Ok("# hello", "text/markdown") : throw new HttpRequestException("blocked") };
		var (engine, _) = EngineFor(transport);
		var result = await engine.OpenAsync("example.com/x", Allowed(), CancellationToken.None);
		Assert.Equal(RelayStrategy.ReaderRelay, result.Strategy);
		Assert.False(result.IsHtml);
		Assert.Equal("# hello", result.Content);
		Assert.Equal("https://r.jina.ai/https://example.com/x", transport.Calls[^1].Request.Url.AbsoluteUri);
		Assert.Equal("https://example.com/x", result.FinalUrl);
	}

	[Fact]
	public async Task With_consent_the_archive_is_asked_for_its_closest_snapshot()
	{
		var transport = new FakeTransport
		{
			Plain = request => request.Url.AbsolutePath == "/wayback/available" ? Ok(WaybackAnswer, "application/json")
				: request.Url.Host == "web.archive.org" ? Ok("old page", "text/html", url: "https://web.archive.org/web/2024/http://example.com/x")
				: throw new HttpRequestException("blocked"),
		};
		var (engine, _) = EngineFor(transport);
		var result = await engine.OpenAsync("example.com/x", Allowed(), CancellationToken.None);
		Assert.Equal(RelayStrategy.Wayback, result.Strategy);
		Assert.Equal("old page", result.Content);
		Assert.Equal("https://web.archive.org/web/2024/http://example.com/x", transport.Calls[^1].Request.Url.AbsoluteUri);
		var lookup = transport.Calls.First(c => c.Request.Url.AbsolutePath == "/wayback/available").Request.Url;
		Assert.Equal("https://example.com/x", HttpUtility.ParseQueryString(lookup.Query)["url"]);
	}

	[Fact]
	public async Task An_archive_without_a_snapshot_is_a_failed_attempt()
	{
		var transport = new FakeTransport { Plain = request => request.Url.AbsolutePath == "/wayback/available" ? Ok("""{"archived_snapshots":{}}""", "application/json") : throw new HttpRequestException("blocked") };
		var (engine, _) = EngineFor(transport);
		var error = await Assert.ThrowsAsync<AllStrategiesFailedError>(() => engine.OpenAsync("example.com/x", Allowed(), CancellationToken.None));
		Assert.Contains("The response came back empty.", error.Message);
	}

	[Fact]
	public async Task No_request_to_anyone_carries_a_cookie_or_sign_in_or_a_browser_look()
	{
		var transport = new FakeTransport
		{
			Plain = request => request.Url.AbsolutePath == "/wayback/available" ? Ok(WaybackAnswer, "application/json")
				: request.Url.Host == "web.archive.org" ? Ok("", "text/html") : throw new HttpRequestException("blocked"),
		};
		var (engine, _) = EngineFor(transport);
		await Assert.ThrowsAsync<AllStrategiesFailedError>(() => engine.OpenAsync("example.com/x", Allowed(), CancellationToken.None));
		Assert.Contains(transport.Calls, c => IsRelay(c.Request));
		Assert.Contains(transport.Calls, c => IsArchive(c.Request));
		Assert.All(transport.Calls, call =>
		{
			var names = call.Request.Headers.Keys.Select(k => k.ToLowerInvariant()).ToList();
			Assert.DoesNotContain("cookie", names);
			Assert.DoesNotContain("authorization", names);
			var agent = call.Request.Headers["User-Agent"];
			Assert.DoesNotContain("Chrome", agent);
			Assert.DoesNotContain("Mozilla", agent);
			Assert.StartsWith("Zing", agent);
		});
	}

	[Fact]
	public async Task All_failing_raises_a_list_of_each_reason()
	{
		var transport = new FakeTransport();
		var (engine, seen) = EngineFor(transport);
		var error = await Assert.ThrowsAsync<AllStrategiesFailedError>(() => engine.OpenAsync("example.com/x", Allowed(), CancellationToken.None));
		var failed = error.Attempts.Where(a => a.Status == AttemptStatus.Failed).ToList();
		Assert.Equal(["Direct", "DohPinned", "ReaderRelay", "Wayback"], failed.Select(a => a.Strategy.ToString()));
		Assert.Contains("Direct: no route to example.com", error.Message);
		Assert.Contains("Wayback archive: no route to archive.org", error.Message);
		Assert.Equal(8, seen.Count);
	}

	[Theory]
	[InlineData("not a url")]
	[InlineData("")]
	[InlineData("ftp://example.com/")]
	[InlineData("https://user:secret@example.com/")]
	public async Task A_bad_address_raises_before_anything_is_tried(string input)
	{
		var transport = new FakeTransport();
		var (engine, seen) = EngineFor(transport);
		await Assert.ThrowsAnyAsync<RelayError>(() => engine.OpenAsync(input, Allowed(), CancellationToken.None));
		Assert.Empty(transport.Calls);
		Assert.Empty(seen);
	}

	[Fact]
	public async Task A_bad_address_raises_BadURLError_without_repeating_what_was_typed()
	{
		var error = await Assert.ThrowsAsync<BadURLError>(() => EngineFor(new FakeTransport()).Engine.OpenAsync("not a url", new RelayConsent(), CancellationToken.None));
		Assert.DoesNotContain("not a url", error.Message);
	}

	[Fact]
	public async Task A_hung_attempt_times_out_and_the_engine_moves_on()
	{
		var transport = new FakeTransport { Plain = Hangs, Pinned = _ => Ok(Page) };
		var (engine, seen) = EngineFor(transport, timeouts: new RelayTimeouts(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50)));
		var result = await engine.OpenAsync("example.com/x", new RelayConsent(), CancellationToken.None);
		Assert.Equal(RelayStrategy.DohPinned, result.Strategy);
		Assert.Equal("Didn't answer in time.", seen[1].Reason);
	}

	[Fact]
	public async Task The_whole_run_has_a_deadline()
	{
		var transport = new FakeTransport { Plain = Hangs, Pinned = Hangs };
		var (engine, _) = EngineFor(transport, timeouts: new RelayTimeouts(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)), deadline: TimeSpan.FromMilliseconds(100));
		await Assert.ThrowsAsync<RelayTimedOutError>(() => engine.OpenAsync("example.com/x", Allowed(), CancellationToken.None));
	}

	[Fact]
	public async Task Cancelling_stops_the_run_instead_of_trying_the_next_method()
	{
		using var cancel = new CancellationTokenSource();
		var transport = new FakeTransport
		{
			Plain = request =>
			{
				cancel.Cancel();
				return Hangs(request);
			},
		};
		var (engine, _) = EngineFor(transport);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.OpenAsync("example.com/x", Allowed(), cancel.Token));
		Assert.Single(transport.Calls);
	}

	[Fact]
	public async Task A_programming_error_in_a_transport_is_not_hidden_as_a_failed_attempt()
	{
		var transport = new FakeTransport { Plain = _ => throw new InvalidOperationException("a bug") };
		var (engine, _) = EngineFor(transport);
		await Assert.ThrowsAsync<InvalidOperationException>(() => engine.OpenAsync("example.com/x", Allowed(), CancellationToken.None));
	}

	[Fact]
	public void Consent_starts_off_and_is_in_memory_only()
	{
		var consent = new RelayConsent();
		Assert.False(consent.ThirdPartiesAllowed);
		consent.Allow();
		Assert.True(consent.ThirdPartiesAllowed);
	}

	[Fact]
	public void The_default_timeouts_are_35_seconds_for_pages_and_30_for_the_relay_and_archive_snapshot()
	{
		Assert.Equal(TimeSpan.FromSeconds(35), RelayTimeouts.Default.Page);
		Assert.Equal(TimeSpan.FromSeconds(30), RelayTimeouts.Default.Relay);
	}

	[Fact]
	public void The_worst_case_of_every_attempt_timing_out_fits_inside_the_run_deadline()
	{
		var t = RelayTimeouts.Default;
		// direct, pinned, reader relay, then the archive's lookup and its snapshot.
		var worst = t.Page + t.Page + t.Relay + t.Page + t.Relay;
		Assert.True(worst < RelayEngine.DefaultDeadline, $"{worst} should be under {RelayEngine.DefaultDeadline}");
	}

	[Fact]
	public async Task The_reader_relay_gets_the_relay_timeout_not_the_page_one()
	{
		var transport = new FakeTransport { Plain = request => IsRelay(request) ? Ok("text") : Hangs(request) };
		var (engine, seen) = EngineFor(transport, new RelayTimeouts(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(10)));
		var result = await engine.OpenAsync("example.com/x", Allowed(), CancellationToken.None);
		Assert.Equal(RelayStrategy.ReaderRelay, result.Strategy);
		Assert.Equal("Didn't answer in time.", seen[1].Reason);
	}

	[Fact]
	public async Task The_archive_lookup_has_the_page_timeout_and_the_snapshot_its_own_relay_timeout()
	{
		var lookupHangs = new FakeTransport { Plain = request => IsArchive(request) ? Hangs(request) : throw new HttpRequestException("blocked") };
		var (slowLookup, _) = EngineFor(lookupHangs, new RelayTimeouts(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(30)));
		var watch = System.Diagnostics.Stopwatch.StartNew();
		var lookupError = await Assert.ThrowsAsync<AllStrategiesFailedError>(() => slowLookup.OpenAsync("example.com/x", Allowed(), CancellationToken.None));
		Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "the lookup should have been cut off at the page timeout");
		Assert.Contains("Wayback archive: Didn't answer in time.", lookupError.Message);

		var snapshotHangs = new FakeTransport
		{
			Plain = request => request.Url.AbsolutePath == "/wayback/available" ? Ok(WaybackAnswer, "application/json")
				: request.Url.Host == "web.archive.org" ? Hangs(request) : throw new HttpRequestException("blocked"),
		};
		var (slowSnapshot, _) = EngineFor(snapshotHangs, new RelayTimeouts(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100)));
		watch.Restart();
		var snapshotError = await Assert.ThrowsAsync<AllStrategiesFailedError>(() => slowSnapshot.OpenAsync("example.com/x", Allowed(), CancellationToken.None));
		Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "the snapshot should have been cut off at the relay timeout");
		Assert.Contains("Wayback archive: Didn't answer in time.", snapshotError.Message);
	}

	[Theory]
	[InlineData("http://192.168.1.5/x")]
	[InlineData("localhost:3000")]
	[InlineData("http://printer.local/")]
	[InlineData("intranet/wiki")]
	public async Task An_address_on_the_users_own_network_is_never_sent_to_a_third_party_even_with_consent(string input)
	{
		var transport = new FakeTransport();
		var (engine, seen) = EngineFor(transport);
		var error = await Assert.ThrowsAsync<AllStrategiesFailedError>(() => engine.OpenAsync(input, Allowed(), CancellationToken.None));
		Assert.DoesNotContain(transport.Calls, c => IsRelay(c.Request) || IsArchive(c.Request) || c.Via == "pinned");
		Assert.Equal(["Direct:Trying", "Direct:Failed", "DohPinned:Skipped", "ReaderRelay:Skipped", "Wayback:Skipped"], Describe(seen));
		Assert.Contains("your own network", seen[2].Reason);
		Assert.Contains("your own network", seen[3].Reason);
		Assert.DoesNotContain("Reader relay", error.Message);
	}

	[Fact]
	public async Task An_address_on_the_users_own_network_does_not_ask_for_consent_that_would_not_help()
	{
		var (engine, _) = EngineFor(new FakeTransport());
		await Assert.ThrowsAsync<AllStrategiesFailedError>(() => engine.OpenAsync("http://192.168.1.5/", new RelayConsent(), CancellationToken.None));
	}

	[Fact]
	public async Task An_address_on_the_users_own_network_can_still_open_directly()
	{
		var transport = new FakeTransport { Plain = _ => Ok(Page) };
		var (engine, _) = EngineFor(transport);
		var result = await engine.OpenAsync("http://192.168.1.5/", Allowed(), CancellationToken.None);
		Assert.Equal(RelayStrategy.Direct, result.Strategy);
	}

	[Fact]
	public void Private_hosts_match_the_shared_vectors()
	{
		foreach (var v in Vectors.Load(VectorsPath).GetProperty("privateHosts").EnumerateArray())
			Assert.Equal(v.GetProperty("private").GetBoolean(), RelayRules.IsPrivateHost(RelayRules.Normalize(v.GetProperty("input").GetString()!)));
	}

	[Fact]
	public void Private_host_names_match_the_shared_vectors()
	{
		foreach (var v in Vectors.Load(VectorsPath).GetProperty("privateHostNames").EnumerateArray())
			Assert.Equal(v.GetProperty("private").GetBoolean(), RelayRules.IsPrivateHostName(v.GetProperty("host").GetString()!));
	}

	[Fact]
	public void An_address_with_an_empty_label_is_refused_rather_than_sent_anywhere()
	{
		Assert.Throws<BadURLError>(() => RelayRules.Normalize("localhost.."));
	}

	[Fact]
	public void Credentials_in_an_address_match_the_shared_vectors()
	{
		foreach (var input in Vectors.Load(VectorsPath).GetProperty("credentials").EnumerateArray().Select(e => e.GetString()!))
			Assert.Throws<CredentialsInURLError>(() => RelayRules.Normalize(input));
	}

	public static TheoryData<string> Normalized()
	{
		var data = new TheoryData<string>();
		foreach (var v in Vectors.Load(VectorsPath).GetProperty("normalize").EnumerateArray()) data.Add(v.GetRawText());
		return data;
	}

	[Theory]
	[MemberData(nameof(Normalized))]
	public void Normalizing_matches_the_shared_vectors(string vector)
	{
		var v = JsonDocument.Parse(vector).RootElement;
		Assert.Equal(v.GetProperty("href").GetString(), RelayRules.Normalize(v.GetProperty("input").GetString()!).AbsoluteUri);
	}

	[Fact]
	public void Refusals_match_the_shared_vectors()
	{
		foreach (var input in Vectors.Load(VectorsPath).GetProperty("refused").EnumerateArray().Select(e => e.GetString()!))
			Assert.Throws<BadURLError>(() => RelayRules.Normalize(input));
	}

	[Fact]
	public void Statuses_match_the_shared_vectors()
	{
		var status = Vectors.Load(VectorsPath).GetProperty("status");
		foreach (var code in status.GetProperty("ok").EnumerateArray()) RelayRules.ValidateStatus(code.GetInt32());
		foreach (var code in status.GetProperty("failed").EnumerateArray())
			Assert.Equal(code.GetInt32(), Assert.Throws<HTTPStatusError>(() => RelayRules.ValidateStatus(code.GetInt32())).Status);
	}

	[Fact]
	public void Wayback_snapshots_match_the_shared_vectors()
	{
		var vectors = Vectors.Load(VectorsPath);
		foreach (var v in vectors.GetProperty("waybackSnapshot").EnumerateArray())
			Assert.Equal(v.GetProperty("href").GetString(), RelayRules.WaybackSnapshot(v.GetProperty("answer")).AbsoluteUri);
		foreach (var answer in vectors.GetProperty("waybackEmpty").EnumerateArray())
			Assert.Throws<EmptyBodyError>(() => RelayRules.WaybackSnapshot(answer));
	}

	[Fact]
	public async Task A_name_that_resolves_to_the_own_network_ends_the_run_with_that_error_and_asks_no_third_party()
	{
		var transport = new FakeTransport { Plain = _ => throw new HttpRequestException("connect", new OwnNetworkError()) };
		var (engine, _) = EngineFor(transport);
		await Assert.ThrowsAsync<OwnNetworkError>(() => engine.OpenAsync("example.com/x", Allowed(), CancellationToken.None));
		Assert.Single(transport.Calls);
	}

	[Fact]
	public async Task A_refusal_from_the_pinned_attempt_also_ends_the_run()
	{
		var transport = new FakeTransport { Pinned = _ => throw new OwnNetworkError() };
		var (engine, _) = EngineFor(transport);
		await Assert.ThrowsAsync<OwnNetworkError>(() => engine.OpenAsync("example.com/x", Allowed(), CancellationToken.None));
		Assert.DoesNotContain(transport.Calls, c => IsRelay(c.Request) || IsArchive(c.Request));
	}

	[Fact]
	public async Task When_every_method_fails_the_message_names_each_one_tried()
	{
		var (engine, _) = EngineFor(new FakeTransport());
		var error = await Assert.ThrowsAsync<AllStrategiesFailedError>(() => engine.OpenAsync("example.com/x", Allowed(), CancellationToken.None));
		foreach (var label in new[] { "Direct", "DoH + IP pinning", "Reader relay", "Wayback archive" }) Assert.Contains(label, error.Message);
	}

	[Fact]
	public async Task A_resource_never_goes_to_the_reader_relay_which_answers_with_text()
	{
		var transport = new FakeTransport();
		var (engine, _) = EngineFor(transport);
		await Assert.ThrowsAsync<AllStrategiesFailedError>(() => engine.OpenResourceAsync(new Uri("https://example.com/a.css"), Allowed(), CancellationToken.None));
		Assert.DoesNotContain(transport.Calls, c => IsRelay(c.Request));
	}

	[Fact]
	public async Task A_resource_reaches_the_archive_only_with_consent()
	{
		var transport = new FakeTransport();
		var (engine, seen) = EngineFor(transport);
		var result = await engine.OpenResourceAsync(new Uri("https://example.com/a.css"), new RelayConsent(), CancellationToken.None);
		Assert.Equal(RelayOutcome.NeedsConsent, result.Outcome);
		Assert.DoesNotContain(transport.Calls, c => IsArchive(c.Request));
		Assert.Contains("Wayback:Skipped", Describe(seen));
	}

	[Fact]
	public async Task A_resource_on_the_own_network_never_reaches_a_third_party_even_with_consent()
	{
		var transport = new FakeTransport();
		var (engine, _) = EngineFor(transport);
		await Assert.ThrowsAsync<AllStrategiesFailedError>(() => engine.OpenResourceAsync(new Uri("https://intranet/a.css"), Allowed(), CancellationToken.None));
		Assert.DoesNotContain(transport.Calls, c => IsArchive(c.Request) || IsRelay(c.Request));
	}

	[Fact]
	public async Task A_resource_address_with_credentials_is_refused()
	{
		var (engine, _) = EngineFor(new FakeTransport());
		await Assert.ThrowsAsync<CredentialsInURLError>(() => engine.OpenResourceAsync(new Uri("https://u:p@example.com/a.css"), Allowed(), CancellationToken.None));
	}
}
