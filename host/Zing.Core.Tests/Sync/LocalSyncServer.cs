extern alias ZingServer;

using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Zing.Core.Sync;

namespace Zing.Core.Tests.Sync;

/// <summary>
/// The real sync server (server/Zing.Server) in process, with its fake Google issuer and throwaway values, as the server's own
/// tests and CI run it. Nothing reaches Google or Railway. <see cref="Handler"/> carries every request through <see cref="Wire"/>.
/// </summary>
/// <param name="clock">The server's clock, for a test that moves it on; null is the real one.</param>
internal sealed class LocalSyncServer(TimeProvider? clock = null) : WebApplicationFactory<ZingServer::Program>
{
	public const string AllowedSub = "sub-sync-test";
	/// <summary>A second Google account the server allows, for a PC signed in as someone else.</summary>
	public const string OtherSub = "sub-sync-other";
	public const string Email = "ada@example.test";

	private readonly string _dbDir = Directory.CreateTempSubdirectory("zing-sync-server-").FullName;

	public Uri Address => new("http://localhost/");

	/// <summary>Watches and tampers with requests; shared by every handler this server hands out.</summary>
	public Wire Wire { get; } = new();

	public HttpMessageHandler Handler()
	{
		Wire.InnerHandler ??= Server.CreateHandler();
		return Wire;
	}

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		builder.UseEnvironment("Staging");
		builder.UseSetting("ZING_SERVER_FAKE_GOOGLE", "1");
		builder.UseSetting("ZING_SESSION_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
		builder.UseSetting("ZING_ALLOWED_SUBS", $"{AllowedSub},{OtherSub}");
		builder.UseSetting("ZING_DB_PATH", Path.Combine(_dbDir, "zing.db"));
		builder.UseSetting("ZING_RATE_IP_PER_MINUTE", "100000");
		builder.UseSetting("ZING_RATE_USER_PER_MINUTE", "100000");
		builder.UseContentRoot(_dbDir);
		if (clock is null) return;
		builder.ConfigureTestServices(services => services.AddSingleton(clock));
	}

	private string DbPath => Path.Combine(_dbDir, "zing.db");

	/// <summary>A copy of the server's whole database as it is now, as a backup would take it. Returns where it is.</summary>
	public string Backup()
	{
		var path = Path.Combine(_dbDir, $"backup-{Guid.NewGuid():N}.db");
		using var live = OpenDb(DbPath);
		using var copy = OpenDb(path);
		live.BackupDatabase(copy);
		return path;
	}

	/// <summary>The server restored from <paramref name="backup"/>: everything since that backup is gone from it.</summary>
	public void Restore(string backup)
	{
		using var copy = OpenDb(backup);
		using var live = OpenDb(DbPath);
		copy.BackupDatabase(live);
	}

	private static SqliteConnection OpenDb(string path)
	{
		var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
		connection.Open();
		return connection;
	}

	/// <summary>Every byte of the server's database as it is on disk, its write-ahead log included: what someone who stole it would have.</summary>
	public byte[] StoredBytes()
	{
		var db = Path.Combine(_dbDir, "zing.db");
		return [.. Read(db), .. Read(db + "-wal")];

		static byte[] Read(string path)
		{
			if (!File.Exists(path)) return [];
			using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using var copy = new MemoryStream();
			file.CopyTo(copy);
			return copy.ToArray();
		}
	}

	/// <summary>An ID token from the fake issuer, as e2e's stand-in for Google gets one.</summary>
	public async Task<string> MintIdTokenAsync(string sub = AllowedSub, string email = Email)
	{
		using var client = CreateClient();
		var response = await client.PostAsJsonAsync("/fake-google/token", new { sub, email });
		response.EnsureSuccessStatusCode();
		return (await response.Content.ReadFromJsonAsync<MintedToken>(SyncJson.Options))!.IdToken;
	}

	protected override void Dispose(bool disposing)
	{
		base.Dispose(disposing);
		if (!disposing) return;
		SqliteConnection.ClearAllPools();
		// The factory disposes twice (DisposeAsync, then Dispose); the second finds the folder already gone.
		if (Directory.Exists(_dbDir)) Directory.Delete(_dbDir, true);
	}

	private sealed record MintedToken(string IdToken);
}

/// <summary>Sits between the client and the server: logs each request, and can swap the session for a junk one so the server answers 401.</summary>
internal sealed class Wire : DelegatingHandler
{
	private readonly object _gate = new();

	/// <summary>"GET /keys" and the like, in the order they were sent, and anything a test adds with <see cref="Note"/>.</summary>
	public List<string> Log { get; } = [];

	/// <summary>How many of the next session-carrying requests get a junk session.</summary>
	public int SpoilNext { get; set; }

	/// <summary>Every session-carrying request gets a junk session.</summary>
	public bool SpoilAll { get; set; }

	/// <summary>The server can't be reached: every request fails before it is sent.</summary>
	public bool Down { get; set; }

	/// <summary>The next refresh reaches the server and is answered, but the answer is lost on the way back.</summary>
	public bool LoseNextRefreshAnswer { get; set; }

	/// <summary>The next request with this line ("PUT /keys") reaches the server and is answered, but the answer is lost on the way back.</summary>
	public string? LoseNextAnswerTo { get; set; }

	/// <summary>Requests this says yes to fail before they are sent, as a dropped connection would.</summary>
	public Func<string, bool>? FailWhen { get; set; }

	/// <summary>Awaited before each request is sent, with its line: a test holds a request here to start something else meanwhile.</summary>
	public Func<string, Task>? BeforeSend { get; set; }

	/// <summary>Rewrites the body of each answer, given its request line: a server that tampers with what it sends.</summary>
	public Func<string, string, string>? Rewrite { get; set; }

	/// <summary>Runs once the server has answered a refresh, before the client sees the answer.</summary>
	public Action? AfterRefreshAnswered { get; set; }

	public int Refreshes => Count("POST /auth/refresh");

	public int Count(string line)
	{
		lock (_gate) return Log.Count(l => l == line);
	}

	public void Note(string line)
	{
		lock (_gate) Log.Add(line);
	}

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		var line = $"{request.Method} {request.RequestUri!.AbsolutePath}";
		Note(line);
		if (BeforeSend is { } before) await before(line);
		if (Down) throw new HttpRequestException("The test's sync server is down");
		if (FailWhen?.Invoke(line) == true) throw new HttpRequestException($"The test dropped {line}");
		if (request.Headers.Authorization is not null && Spoil()) request.Headers.Authorization = new("Bearer", "user.1.junk");
		var response = await base.SendAsync(request, cancellationToken);
		if (line == LoseNextAnswerTo)
		{
			LoseNextAnswerTo = null;
			response.Dispose();
			throw new HttpRequestException($"The test lost the answer to {line}");
		}
		if (Rewrite is { } rewrite && response.Content is { } content)
		{
			var body = await content.ReadAsStringAsync(cancellationToken);
			response.Content = new StringContent(rewrite(line, body), System.Text.Encoding.UTF8, "application/json");
		}
		if (line != "POST /auth/refresh") return response;
		AfterRefreshAnswered?.Invoke();
		if (!LoseNextRefreshAnswer) return response;
		LoseNextRefreshAnswer = false;
		response.Dispose();
		throw new HttpRequestException("The test lost the refresh answer");
	}

	private bool Spoil()
	{
		lock (_gate)
		{
			if (SpoilAll) return true;
			if (SpoilNext == 0) return false;
			SpoilNext--;
			return true;
		}
	}
}

/// <summary>Holds the account in memory, and logs each save into the wire's log so a test sees it against the requests.</summary>
internal sealed class MemoryAccountStore(Wire? wire = null) : ISyncAccountStore
{
	private SyncAccount? _account;

	public int Saves { get; private set; }

	public SyncAccount? SyncAccount() => _account;

	public void SaveSyncAccount(SyncAccount account)
	{
		Saves++;
		wire?.Note("save account");
		_account = account;
	}

	public bool SignedOutByServer { get; private set; }

	public void ForgetSyncAccount() => _account = null;

	public void MarkSyncSignedOut()
	{
		_account = null;
		SignedOutByServer = true;
	}
}
