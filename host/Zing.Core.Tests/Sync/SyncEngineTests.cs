using Zing.Core.Sync;
using Zing.Core.Vault;

namespace Zing.Core.Tests.Sync;

/// <summary>The sync engine against the real server (fake Google issuer) and two stores that share one vault's keys.</summary>
public sealed class SyncEngineTests : IDisposable
{
	private readonly TwoPcs _pcs;

	public SyncEngineTests() => _pcs = new TwoPcs();

	public void Dispose() => _pcs.Dispose();

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	private SyncPc A => _pcs.A;

	private SyncPc B => _pcs.B;

	private static readonly TimeSpan Days31 = TimeSpan.FromDays(31);

	[Fact]
	public async Task AnEditOnAAppearsOnB()
	{
		var id = await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		await _pcs.JoinBAsync();
		Assert.Equal("ada", B.Login(id).Username);
		Assert.Equal("first-password", await B.Vault.PasswordAsync(id));

		A.Vault.UpdateSummary(id, "My bank", "joint account", "ada.lovelace");
		await A.SyncAsync();
		await B.SyncAsync();

		var onB = B.Login(id);
		Assert.Equal(("My bank", "joint account", "ada.lovelace"), (onB.Title, onB.Note, onB.Username));
		Assert.Empty(B.Vault.Unsent());
	}

	[Fact]
	public async Task ADeleteOnAShowsInRecentlyDeletedOnBAndARestoreOnBComesBackToA()
	{
		var id = await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		await _pcs.JoinBAsync();

		A.Vault.Delete(id);
		await A.SyncAsync();
		await B.SyncAsync();
		Assert.Contains(VaultModel.RecentlyDeleted(B.Vault.Summaries(), B.Clock.Now()), s => s.Id == id);

		B.Vault.Restore(id);
		await B.SyncAsync();
		await A.SyncAsync();

		Assert.Null(A.Login(id).DeletedAt);
		Assert.Equal("first-password", await A.Vault.PasswordAsync(id));
	}

	[Fact]
	public async Task APasswordChangedOnBAfterADeleteOnARevivesTheLoginWithTheNewPassword()
	{
		var id = await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		await _pcs.JoinBAsync();

		A.Vault.Delete(id);
		B.Clock.Advance(TimeSpan.FromMinutes(1));
		await B.Vault.UpdatePasswordAsync(id, "changed-after-the-delete");
		await A.SyncAsync();
		await B.SyncAsync();
		await A.SyncAsync();

		foreach (var pc in new[] { A, B })
		{
			Assert.Null(pc.Login(id).DeletedAt);
			Assert.Equal("changed-after-the-delete", await pc.Vault.PasswordAsync(id));
		}
	}

	[Fact]
	public async Task ADeleteOnAAfterAPasswordChangeOnBItHasSyncedStaysDeletedAndKeepsTheNewPassword()
	{
		var id = await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		await _pcs.JoinBAsync();

		await B.Vault.UpdatePasswordAsync(id, "changed-before-the-delete");
		await B.SyncAsync();
		await A.SyncAsync();
		A.Vault.Delete(id);
		await A.SyncAsync();
		await B.SyncAsync();
		await A.SyncAsync();

		foreach (var pc in new[] { A, B })
		{
			Assert.NotNull(pc.Login(id).DeletedAt);
			Assert.Equal("changed-before-the-delete", await pc.Vault.PasswordAsync(id));
		}
	}

	[Fact]
	public async Task APasswordChangedOnBWhileADeletedItRevivesTheLoginEvenWithBsClockAnHourBehind()
	{
		var id = await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		await _pcs.JoinBAsync();

		B.Clock.Advance(TimeSpan.FromHours(-1));
		await B.Vault.UpdatePasswordAsync(id, "changed-on-a-slow-clock");
		A.Vault.Delete(id);
		await A.SyncAsync();
		await B.SyncAsync();
		await A.SyncAsync();

		foreach (var pc in new[] { A, B })
		{
			Assert.Null(pc.Login(id).DeletedAt);
			Assert.Equal("changed-on-a-slow-clock", await pc.Vault.PasswordAsync(id));
		}
	}

	[Fact]
	public async Task ADeleteOnAStaysDeletedWhenBChangedNothingEvenWithBsClockAnHourAhead()
	{
		var id = await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		await _pcs.JoinBAsync();

		B.Clock.Advance(TimeSpan.FromHours(1));
		var login = B.Login(id);
		B.Vault.UpdateSummary(id, login.Title, login.Note, login.Username);
		A.Vault.Delete(id);
		await A.SyncAsync();
		await B.SyncAsync();
		await A.SyncAsync();

		foreach (var pc in new[] { A, B }) Assert.NotNull(pc.Login(id).DeletedAt);
	}

	[Fact]
	public async Task BothChangedDifferentFieldsAndBothPcsEndWithBothChanges()
	{
		var id = await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		await _pcs.JoinBAsync();

		A.Vault.UpdateSummary(id, "Title from A", "", "ada");
		B.Vault.UpdateSummary(id, "Bank", "Note from B", "ada");
		await A.SyncAsync();
		await B.SyncAsync();
		await A.SyncAsync();

		foreach (var pc in new[] { A, B })
		{
			var login = pc.Login(id);
			Assert.Equal(("Title from A", "Note from B"), (login.Title, login.Note));
		}
		Assert.Equal(A.Vault.Records.Single(r => r.ItemId == id).Version, B.Vault.Records.Single(r => r.ItemId == id).Version);
	}

	[Fact]
	public async Task BothChangedThePasswordSoTheNewerWinsAndTheOlderIsInTheHistoryOnBothPcs()
	{
		var id = await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		await _pcs.JoinBAsync();

		await A.Vault.UpdatePasswordAsync(id, "changed-on-a");
		B.Clock.Advance(TimeSpan.FromMinutes(1));
		await B.Vault.UpdatePasswordAsync(id, "changed-on-b-later");
		await A.SyncAsync();
		var hello = A.Sealer.Calls + B.Sealer.Calls;
		await B.SyncAsync();
		await A.SyncAsync();

		Assert.Equal(hello, A.Sealer.Calls + B.Sealer.Calls);
		foreach (var pc in new[] { A, B })
		{
			Assert.Equal("changed-on-b-later", await pc.Vault.PasswordAsync(id));
			var history = pc.Vault.History(id);
			var old = new List<string>();
			for (var i = 0; i < history.Count; i++) old.Add(await pc.Vault.HistoryPasswordAsync(id, i));
			Assert.Equal(["changed-on-a", "first-password"], old);
			// The manage page's "2 earlier passwords" follows the merge, without a local password change.
			Assert.Equal(2, pc.Vault.Summaries().Single().HistoryCount);
		}
	}

	[Fact]
	public async Task SyncingNeedsNoWindowsHello()
	{
		var id = await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		await _pcs.JoinBAsync();
		A.Vault.UpdateSummary(id, "Edited", "", "ada");
		B.Vault.UpdateSummary(id, "Bank", "Also edited", "ada");
		A.Sealer.Result = Vault.HelloResult.Cancel;
		B.Sealer.Result = Vault.HelloResult.Cancel;
		var hello = A.Sealer.Calls + B.Sealer.Calls;

		await A.SyncAsync();
		await B.SyncAsync();
		await A.SyncAsync();

		Assert.Equal(hello, A.Sealer.Calls + B.Sealer.Calls);
		Assert.Equal(("Edited", "Also edited"), (A.Login(id).Title, A.Login(id).Note));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task APulledPageThatCouldNotBeStoredIsPulledAgainAndNotTakenAsWithheld(bool restart)
	{
		await _pcs.TurnOnAAsync();
		await _pcs.JoinBAsync();
		var id = await _pcs.SaveOnAAsync();
		await A.SyncAsync();

		// Antivirus or OneDrive holding vault.zing (I1): the replace fails after the page was checked.
		using (new FileStream(Path.Combine(B.Dir, VaultStore.VaultFileName), FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			await Assert.ThrowsAsync<UnauthorizedAccessException>(B.SyncAsync);
		}
		if (restart) B.Restart();
		await B.SyncAsync();

		Assert.Equal("ada", B.Login(id).Username);
		Assert.Equal("first-password", await B.Vault.PasswordAsync(id));
		Assert.Null(B.Engine.Status.Error);
	}

	[Fact]
	public async Task TurningSyncOffWaitsForASyncRunningAndLeavesNoSyncStateBehindIt()
	{
		await _pcs.TurnOnAAsync();
		var id = await _pcs.SaveOnAAsync();
		var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_pcs.Server.Wire.BeforeSend = async line =>
		{
			if (line != "POST /changes" || reached.Task.IsCompleted) return;
			reached.SetResult();
			await release.Task;
		};
		var sync = A.SyncAsync();
		await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

		var off = SyncKeys.TurnOffAsync(A.Vault, A.Client, Ct);
		await SyncKeys.QueuedBehind(A.Vault).WaitAsync(TimeSpan.FromSeconds(10), Ct);
		Assert.False(off.IsCompleted);
		release.SetResult();
		await sync;
		await off;

		Assert.Null(A.Vault.SyncKeys);
		Assert.Null(A.Vault.SyncAccount());
		Assert.Equal(id, Assert.Single(A.Vault.Unsent()).Record.ItemId);
		Assert.Equal("first-password", await A.Vault.PasswordAsync(id));
	}

	[Fact]
	public async Task ASignInFromAnotherWindowDuringTurningSyncOffIsKeptNotForgottenUnrevoked()
	{
		await _pcs.TurnOnAAsync();
		var old = A.Vault.SyncAccount()!;
		Task? signIn = null;
		_pcs.Server.Wire.BeforeSend = line =>
		{
			// Another window signs in while this one signs out for Turn off (N3 of the final re-review).
			if (line == "POST /auth/signout" && signIn is null) signIn = A.SignInAsync();
			return Task.CompletedTask;
		};

		Assert.True(await SyncKeys.TurnOffAsync(A.Vault, A.Client, Ct));
		await signIn!;

		Assert.Null(A.Vault.SyncKeys);
		var kept = A.Vault.SyncAccount();
		Assert.NotNull(kept);
		Assert.NotEqual(old.Refresh, kept.Refresh);
		Assert.Equal(1, _pcs.Server.Wire.Count("POST /auth/signout"));
	}

	[Fact]
	public async Task APcPastTheHorizonResyncsFullyAndADeletedLoginDoesNotComeBack()
	{
		using var pcs = await PurgedWhileBWasAway(editOnB: false);
		var (a, b, id) = (pcs.A, pcs.B, pcs.Id);

		await b.SyncAsync();

		Assert.DoesNotContain(b.Vault.Summaries(), s => s.Id == id);
		Assert.Empty(b.Vault.Unsent());
		await a.SyncAsync();
		Assert.DoesNotContain(a.Vault.Summaries(), s => s.Id == id);
		Assert.DoesNotContain((await pcs.Pcs.ServerItemsAsync()).Items, i => i.ItemId == id);
	}

	[Fact]
	public async Task AnEditMadeOfflineToALoginPurgedMeanwhileIsMadeAgainOnEveryPc()
	{
		using var pcs = await PurgedWhileBWasAway(editOnB: true);
		var (a, b, id) = (pcs.A, pcs.B, pcs.Id);

		await b.SyncAsync();
		await a.SyncAsync();

		Assert.Equal("Edited while away", b.Login(id).Title);
		Assert.Equal("Edited while away", a.Login(id).Title);
		Assert.Equal("first-password", await a.Vault.PasswordAsync(id));
	}

	/// <summary>
	/// B synced login X, then went away. A deleted X; 31 days on, A's sync made it a tombstone, and 31 more days on the server purged
	/// it, moving its horizon past B's cursor. B may have edited X while away.
	/// </summary>
	private static async Task<Purged> PurgedWhileBWasAway(bool editOnB)
	{
		var clock = new ServerClock();
		var pcs = new TwoPcs(clock);
		var id = await pcs.SaveOnAAsync();
		await pcs.TurnOnAAsync();
		await pcs.JoinBAsync();

		pcs.A.Vault.Delete(id);
		await pcs.A.SyncAsync();
		pcs.A.Clock.Advance(Days31);
		await pcs.A.SyncAsync();
		Assert.True(pcs.A.Vault.Records.Single(r => r.ItemId == id).Tombstone);
		clock.Advance(Days31);
		await pcs.A.SyncAsync();
		Assert.DoesNotContain((await pcs.ServerItemsAsync()).Items, i => i.ItemId == id);
		if (editOnB) pcs.B.Vault.UpdateSummary(id, "Edited while away", "", "ada");
		return new Purged(pcs, id);
	}

	private sealed record Purged(TwoPcs Pcs, string Id) : IDisposable
	{
		public SyncPc A => Pcs.A;
		public SyncPc B => Pcs.B;

		public void Dispose() => Pcs.Dispose();
	}

	private static IReadOnlyList<NewLogin> Logins(int count) => [.. Enumerable.Range(0, count).Select(i => new NewLogin($"https://site{i}.example.test", $"user{i}", $"pw{i}", "", ""))];

	[Fact]
	public async Task ACrashAfter50Of120LoginsWereUploadedResumesWithoutDuplicates()
	{
		await _pcs.TurnOnAAsync();
		await A.Vault.ImportAsync(Logins(120));
		// The crashed run: the server took 50, and Zing stopped before it stored anything about them.
		var first50 = A.Vault.Unsent().Take(50).Select(u => SyncWire.Push(u)).ToList();
		var pushed = await A.Client.PostChangesAsync(first50, Ct, asUser: A.Vault.SyncKeys!.UserId);
		Assert.Equal(50, pushed.Accepted.Count);
		var versions = A.Vault.Records.ToDictionary(r => r.ItemId, r => r.Version);
		A.Restart();

		await A.SyncAsync();

		var server = await _pcs.ServerItemsAsync();
		Assert.Equal(120, server.Items.Count);
		Assert.Equal(120, server.Items.Select(i => i.ItemId).Distinct().Count());
		Assert.Equal(120, server.Revision);
		Assert.Empty(A.Vault.Unsent());
		Assert.Equal(versions, A.Vault.Records.ToDictionary(r => r.ItemId, r => r.Version));
	}

	[Fact]
	public async Task APushWhoseAnswerIsLostIsNotUploadedTwice()
	{
		await _pcs.TurnOnAAsync();
		await A.Vault.ImportAsync(Logins(120));
		_pcs.Server.Wire.LoseNextAnswerTo = "POST /changes";

		await Assert.ThrowsAsync<HttpRequestException>(A.SyncAsync);
		await A.SyncAsync();

		var server = await _pcs.ServerItemsAsync();
		Assert.Equal(120, server.Items.Count);
		Assert.Equal(120, server.Revision);
		Assert.All(A.Vault.Records, r => Assert.Equal(1, r.Version));
		Assert.Empty(A.Vault.Unsent());
	}

	[Fact]
	public async Task TurningOnStoresTheRevisionsSoTheFirstSyncSendsNothingAgain()
	{
		await A.Vault.ImportAsync(Logins(3));
		await _pcs.TurnOnAAsync();
		Assert.Empty(A.Vault.Unsent());
		Assert.Equal(0, A.Engine.Status.Unsent);
		var sent = _pcs.Server.Wire.Count("POST /changes");

		await A.SyncAsync();

		Assert.Equal(sent, _pcs.Server.Wire.Count("POST /changes"));
		Assert.Equal(1, _pcs.Server.Wire.Count("PUT /manifest"));
	}

	/// <summary>A turn-on whose PUT /keys the server took, from a PC that stopped before it stored the answer (Ruling 53).</summary>
	private async Task PendingTurnOnTheServerTook()
	{
		await A.Vault.ImportAsync(Logins(3));
		await A.SignInAsync();
		var recovery = RecoveryKey.Generate();
		using var keys = await A.Vault.KeyMaterialAsync();
		var blob = SyncKeysBlob.Create(keys, TwoPcs.Strong, recovery, _pcs.Account.State().Profile!, version: 1).ToBytes();
		A.Vault.SavePendingSyncKeys(new VaultPendingKeys(A.Vault.SyncAccount()!.UserId, blob, 0, RecoverySeal.Seal(keys.SecretKey, keys.VaultId, recovery)));
		await A.Client.PutKeysAsync(blob, 0, Ct);
	}

	[Fact]
	public async Task ASyncSettlesPendingKeysFirstAndFinishesTheTurnOnTheyBelongTo()
	{
		await PendingTurnOnTheServerTook();

		await A.SyncAsync();

		Assert.Null(A.Vault.PendingSyncKeys);
		Assert.NotNull(A.Vault.SyncKeys);
		Assert.Equal(3, (await _pcs.ServerItemsAsync()).Items.Count);
		Assert.Equal(1, _pcs.Server.Wire.Count("PUT /manifest"));
		Assert.Empty(A.Vault.Unsent());
	}

	[Fact]
	public async Task ATurnOnSettledByAPasswordChangeIsUploadedByTheNextSync()
	{
		const string next = "labour-muzzle-medici-rushes-fiction7";
		await PendingTurnOnTheServerTook();
		await new SyncKeys(_pcs.Account, A.Vault, A.Client).ChangePasswordAsync(TwoPcs.Strong, next, Ct);
		Assert.Equal(0, _pcs.Server.Wire.Count("POST /changes"));

		await A.SyncAsync();

		Assert.Equal(3, (await _pcs.ServerItemsAsync()).Items.Count);
		Assert.Equal(1, _pcs.Server.Wire.Count("PUT /manifest"));
	}

	[Fact]
	public async Task UnsentLoginsAreCountedUntilTheyUpload()
	{
		await _pcs.TurnOnAAsync();
		await A.Vault.ImportAsync(Logins(2));
		await A.Vault.NeverSave("https://never.example.test");

		Assert.Equal(2, A.Engine.Status.Unsent);
		await A.SyncAsync();
		Assert.Equal(0, A.Engine.Status.Unsent);
	}

	[Fact]
	public async Task SyncRunsAtStartTwoSecondsAfterAChangeAndEveryFiveMinutes()
	{
		await _pcs.TurnOnAAsync();
		var started = A.Engine.NextRun;
		A.Engine.Start();
		Assert.Equal([TimeSpan.Zero], A.Scheduler.Pending);

		A.Scheduler.Fire(TimeSpan.Zero);
		await started.WaitAsync(TimeSpan.FromSeconds(30), Ct);
		Assert.Equal([SyncEngine.Every], A.Scheduler.Pending);

		A.Clock.Advance(SyncEngine.ChangeGap);
		await _pcs.SaveOnAAsync();
		await A.Vault.ImportAsync(Logins(1));
		Assert.Equal([SyncEngine.Every, SyncEngine.AfterChange], A.Scheduler.Pending);
		var afterChange = A.Engine.NextRun;
		A.Scheduler.Fire(SyncEngine.AfterChange);
		await afterChange.WaitAsync(TimeSpan.FromSeconds(30), Ct);
		Assert.Empty(A.Vault.Unsent());
		Assert.Equal([SyncEngine.Every], A.Scheduler.Pending);

		var periodic = A.Engine.NextRun;
		var pulls = _pcs.Server.Wire.Count("GET /changes");
		A.Scheduler.Fire(SyncEngine.Every);
		await periodic.WaitAsync(TimeSpan.FromSeconds(30), Ct);
		Assert.Equal(pulls + 1, _pcs.Server.Wire.Count("GET /changes"));
	}

	/// <summary>A's first sync, run as Zing starting runs it.</summary>
	private async Task StartAAsync()
	{
		var started = A.Engine.NextRun;
		A.Engine.Start();
		A.Scheduler.Fire(TimeSpan.Zero);
		await started.WaitAsync(TimeSpan.FromSeconds(30), Ct);
	}

	[Fact]
	public async Task EditsRightAfterASyncWaitForTheGapSoTheyShareOneSync()
	{
		await _pcs.TurnOnAAsync();
		await StartAAsync();

		await _pcs.SaveOnAAsync();
		await A.Vault.ImportAsync(Logins(1));

		var wait = Assert.Single(A.Scheduler.Pending, d => d != SyncEngine.Every);
		Assert.InRange(wait, SyncEngine.ChangeGap - TimeSpan.FromSeconds(1), SyncEngine.ChangeGap);
		var pushes = _pcs.Server.Wire.Count("POST /changes");
		var afterChange = A.Engine.NextRun;
		A.Scheduler.Fire(wait);
		await afterChange.WaitAsync(TimeSpan.FromSeconds(30), Ct);
		Assert.Equal(pushes + 1, _pcs.Server.Wire.Count("POST /changes"));
		Assert.Empty(A.Vault.Unsent());
	}

	[Fact]
	public async Task A429WaitsAsTheServerSaysSaysSoPlainlyAndThenSyncs()
	{
		await _pcs.TurnOnAAsync();
		_pcs.Server.Wire.AnswerRetryAfter = TimeSpan.FromSeconds(42);
		_pcs.Server.Wire.AnswerWith = r => r.RequestUri!.AbsolutePath == "/manifest" ? System.Net.HttpStatusCode.TooManyRequests : null;
		await StartAAsync();

		Assert.Equal("rate-limited", A.Engine.Status.Error);
		Assert.Equal("The sync server asked Zing to slow down, so it syncs again in 42 seconds.", A.Engine.Status.Message);
		Assert.Equal([TimeSpan.FromSeconds(42)], A.Scheduler.Pending);
		// A change meanwhile waits for the retry too, instead of meeting the limit again.
		await _pcs.SaveOnAAsync();
		Assert.All(A.Scheduler.Pending, d => Assert.True(d > SyncEngine.AfterChange));

		_pcs.Server.Wire.AnswerWith = null;
		var retried = A.Engine.NextRun;
		A.Scheduler.Fire(TimeSpan.FromSeconds(42));
		await retried.WaitAsync(TimeSpan.FromSeconds(30), Ct);
		Assert.Null(A.Engine.Status.Error);
		Assert.Empty(A.Vault.Unsent());
		Assert.Contains(SyncEngine.Every, A.Scheduler.Pending);
	}

	[Fact]
	public async Task A429WithNoRetryAfterBacksOffAMinuteThenTwo()
	{
		await _pcs.TurnOnAAsync();
		_pcs.Server.Wire.AnswerWith = r => r.RequestUri!.AbsolutePath == "/manifest" ? System.Net.HttpStatusCode.TooManyRequests : null;
		await StartAAsync();
		Assert.Equal([SyncEngine.RateLimitedRetry], A.Scheduler.Pending);
		Assert.Equal("The sync server asked Zing to slow down, so it syncs again in a minute.", A.Engine.Status.Message);

		var again = A.Engine.NextRun;
		A.Scheduler.Fire(SyncEngine.RateLimitedRetry);
		await again.WaitAsync(TimeSpan.FromSeconds(30), Ct);

		Assert.Equal([SyncEngine.RateLimitedRetry * 2], A.Scheduler.Pending);
		Assert.Equal("The sync server asked Zing to slow down, so it syncs again in 2 minutes.", A.Engine.Status.Message);
	}

	[Fact]
	public async Task A429DropsTheSyncAChangeAskedForSoNothingRunsBeforeTheRetryAndTheRetryRunsOnce()
	{
		var id = await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		await StartAAsync();
		_pcs.Server.Wire.AnswerRetryAfter = TimeSpan.FromMinutes(2);
		var edited = false;
		_pcs.Server.Wire.AnswerWith = r =>
		{
			if (r.RequestUri!.AbsolutePath != "/manifest") return null;
			if (!edited)
			{
				edited = true;
				// A change mid-sync asks for a sync, which comes due while this one still runs.
				A.Vault.UpdateSummary(id, "Edited mid-sync", "", "ada");
				A.Scheduler.Fire(A.Scheduler.Pending.First(d => d < SyncEngine.Every));
			}
			return System.Net.HttpStatusCode.TooManyRequests;
		};
		var limited = A.Engine.NextRun;
		A.Scheduler.Fire(SyncEngine.Every);
		await limited.WaitAsync(TimeSpan.FromSeconds(30), Ct);

		Assert.Equal([TimeSpan.FromMinutes(2)], A.Scheduler.Pending);
		_pcs.Server.Wire.AnswerWith = null;
		var pulls = _pcs.Server.Wire.Count("GET /changes");
		var retried = A.Engine.NextRun;
		A.Scheduler.Fire(TimeSpan.FromMinutes(2));
		await retried.WaitAsync(TimeSpan.FromSeconds(30), Ct);

		Assert.Equal(pulls + 1, _pcs.Server.Wire.Count("GET /changes"));
		Assert.Empty(A.Vault.Unsent());
		Assert.Equal([SyncEngine.Every], A.Scheduler.Pending);
	}

	[Fact]
	public async Task ASyncChangeDuringA429sWaitWaitsToo()
	{
		await _pcs.TurnOnAAsync();
		_pcs.Server.Wire.AnswerRetryAfter = TimeSpan.FromMinutes(2);
		_pcs.Server.Wire.AnswerWith = r => r.RequestUri!.AbsolutePath == "/manifest" ? System.Net.HttpStatusCode.TooManyRequests : null;
		await StartAAsync();

		A.Vault.TurnOffSync();

		Assert.All(A.Scheduler.Pending, d => Assert.True(d > TimeSpan.FromMinutes(1), $"{d} is before the retry"));
	}

	[Fact]
	public async Task ARefreshLostDuringA429sWaitWaitsToo()
	{
		await _pcs.TurnOnAAsync();
		_pcs.Server.Wire.AnswerRetryAfter = TimeSpan.FromMinutes(2);
		_pcs.Server.Wire.AnswerWith = r => r.RequestUri!.AbsolutePath == "/manifest" ? System.Net.HttpStatusCode.TooManyRequests : null;
		await StartAAsync();
		// The access token is refused, and the refresh it brings gets no answer.
		_pcs.Server.Wire.AnswerWith = r => r.RequestUri!.AbsolutePath == "/manifest" ? System.Net.HttpStatusCode.Unauthorized : null;
		_pcs.Server.Wire.LoseNextRefreshAnswer = true;

		await Assert.ThrowsAnyAsync<Exception>(() => A.Client.GetManifestAsync(Ct, asUser: A.Vault.SyncKeys!.UserId));

		Assert.True(A.Client.RefreshUnanswered);
		Assert.All(A.Scheduler.Pending, d => Assert.True(d > TimeSpan.FromMinutes(1), $"{d} is before the retry"));
	}

	[Fact]
	public async Task TurningSyncOnOrOffRightAfterASyncSyncsWithoutTheChangeGap()
	{
		await _pcs.TurnOnAAsync();
		await StartAAsync();

		A.Vault.TurnOffSync();

		Assert.Contains(SyncEngine.AfterChange, A.Scheduler.Pending);
	}

	[Fact]
	public async Task ARetryAfterOfADayWaitsAnHourAtMost()
	{
		await _pcs.TurnOnAAsync();
		_pcs.Server.Wire.AnswerRetryAfter = TimeSpan.FromDays(1);
		_pcs.Server.Wire.AnswerWith = r => r.RequestUri!.AbsolutePath == "/manifest" ? System.Net.HttpStatusCode.TooManyRequests : null;
		await StartAAsync();

		Assert.Equal(TimeSpan.FromHours(1), SyncEngine.MaxRateLimitedWait);
		Assert.Equal([SyncEngine.MaxRateLimitedWait], A.Scheduler.Pending);
		Assert.Equal("The sync server asked Zing to slow down, so it syncs again in an hour.", A.Engine.Status.Message);
	}

	/// <summary>Each PC's history holds one 130 KiB old password, within the server's 256 KiB; the merge on B keeps both, which isn't.</summary>
	private async Task<string> GiveBALoginTooLargeToSyncAsync()
	{
		var id = await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		await _pcs.JoinBAsync();
		var big = new string('a', 130 * 1024);
		await A.Vault.UpdatePasswordAsync(id, big);
		await A.Vault.UpdatePasswordAsync(id, "a-small-one");
		await B.Vault.UpdatePasswordAsync(id, big.Replace('a', 'b'));
		await B.Vault.UpdatePasswordAsync(id, "b-small-one");
		await A.SyncAsync();
		return id;
	}

	private const string TooLargeWarning = "One login is too large to sync (a long note or many old passwords), so it stays on this PC only. Shorten its note, or delete it if you don't need it.";

	[Fact]
	public async Task ALoginTooLargeToSyncCanStillBeDeletedAndRestoredButNotGrown()
	{
		var id = await GiveBALoginTooLargeToSyncAsync();
		await B.SyncAsync();

		B.Vault.Delete(id);
		B.Vault.Restore(id);
		B.Vault.UpdateSummary(id, "Shorter", "", "ada");
		var refused = await Assert.ThrowsAsync<VaultLoginTooLargeException>(() => B.Vault.UpdatePasswordAsync(id, "one-more"));
		Assert.Equal("This login's earlier passwords already fill all the space sync allows, so Zing can't keep another and nothing was saved. Delete this login, then save the new password as a new login.", refused.Message);
		B.Vault.Delete(id);
		await B.SyncAsync();

		Assert.Null(B.Engine.Status.Error);
		Assert.Equal(TooLargeWarning, B.Engine.Status.Warning);
		Assert.NotNull(B.Login(id).DeletedAt);
	}

	[Fact]
	public async Task AnUploadOfThisPcsCopyWithALoginTooLargeSucceedsAndWarnsApart()
	{
		var id = await GiveBALoginTooLargeToSyncAsync();
		var backup = _pcs.Server.Backup();
		await A.Vault.SaveAsync(new NewLogin("https://mail.example.test", "ada.mail", "mail-secret", "Mail", ""));
		await A.SyncAsync();
		await B.SyncAsync();
		_pcs.Server.Restore(backup);
		await Assert.ThrowsAsync<ServerRolledBackException>(() => B.SyncAsync());

		await B.Engine.UploadLocalCopyAsync(Ct);

		Assert.Null(B.Engine.Status.Error);
		Assert.Equal(TooLargeWarning, B.Engine.Status.Warning);
		Assert.Single(B.Vault.Unsent(), u => u.Record.ItemId == id);
	}

	[Fact]
	public async Task ALoginTooLargeForTheServerIsLeftOutAndEverythingElseStillSyncs()
	{
		var id = await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		await _pcs.JoinBAsync();
		// Each PC's history holds one 130 KiB old password, within the server's 256 KiB; the merge keeps both, which isn't.
		var big = new string('a', 130 * 1024);
		await A.Vault.UpdatePasswordAsync(id, big);
		await A.Vault.UpdatePasswordAsync(id, "a-small-one");
		await B.Vault.UpdatePasswordAsync(id, big.Replace('a', 'b'));
		await B.Vault.UpdatePasswordAsync(id, "b-small-one");
		await A.SyncAsync();
		var other = await B.Vault.SaveAsync(new NewLogin("https://mail.example.test", "ada.mail", "mail-secret", "Mail", ""));

		await B.SyncAsync();

		Assert.Null(B.Engine.Status.Error);
		Assert.Equal(TooLargeWarning, B.Engine.Status.Warning);
		await A.SyncAsync();
		Assert.Null(A.Engine.Status.Error);
		Assert.Equal("mail-secret", await A.Vault.PasswordAsync(other));
	}

	[Fact]
	public async Task ABatchIsSplitBySizeSoTheServerTakesEveryLogin()
	{
		await _pcs.TurnOnAAsync();
		// About 200 KiB each: twenty-five of them are more than one 4 MiB body.
		await A.Vault.ImportAsync([.. Enumerable.Range(0, 25).Select(i => new NewLogin($"https://big{i}.example.test", "ada", "pw", "", new string('n', 200 * 1024)))]);
		var pushes = _pcs.Server.Wire.Count("POST /changes");

		await A.SyncAsync();

		Assert.True(_pcs.Server.Wire.Count("POST /changes") - pushes >= 2);
		Assert.Empty(A.Vault.Unsent());
		Assert.Equal(25, (await _pcs.ServerItemsAsync()).Items.Count);
	}

	[Fact]
	public async Task ARefreshWhoseAnswerIsLostIsRetriedWithinTwoMinutesSoNoPcIsSignedOut()
	{
		await _pcs.TurnOnAAsync();
		A.Restart();
		_pcs.Server.Wire.LoseNextRefreshAnswer = true;
		var lost = A.Engine.NextRun;
		A.Engine.Start();
		A.Scheduler.Fire(TimeSpan.Zero);
		await lost.WaitAsync(TimeSpan.FromSeconds(30), Ct);
		Assert.Equal("unreachable", A.Engine.Status.Error);
		Assert.Contains(SyncEngine.RefreshRetry, A.Scheduler.Pending);
		Assert.True(SyncEngine.RefreshRetry < TimeSpan.FromMinutes(2));

		var retried = A.Engine.NextRun;
		A.Scheduler.Fire(SyncEngine.RefreshRetry);
		await retried.WaitAsync(TimeSpan.FromSeconds(30), Ct);

		Assert.Null(A.Engine.Status.Error);
		Assert.NotNull(A.Vault.SyncAccount());
		Assert.False(A.Vault.SyncSignedOutByServer);
		Assert.Equal(2, _pcs.Server.Wire.Refreshes);
	}

	[Fact]
	public async Task ARefreshLostOutsideASyncAlsoBringsTheNextSyncWithinTheRetryWindow()
	{
		await _pcs.TurnOnAAsync();
		A.Restart();
		A.Engine.Start();
		_pcs.Server.Wire.LoseNextRefreshAnswer = true;

		// Anything the shared client does, such as a sign-out or a password change, can be what loses it (M12).
		await Assert.ThrowsAsync<HttpRequestException>(() => A.Client.GetManifestAsync(Ct, asUser: A.Vault.SyncKeys!.UserId));

		Assert.Contains(SyncEngine.RefreshRetry, A.Scheduler.Pending);
		var retried = A.Engine.NextRun;
		A.Scheduler.Fire(SyncEngine.RefreshRetry);
		await retried.WaitAsync(TimeSpan.FromSeconds(30), Ct);
		Assert.Null(A.Engine.Status.Error);
		Assert.False(A.Vault.SyncSignedOutByServer);
	}

	[Fact]
	public async Task ATombstoneWaitingToUploadIsNotCountedAsALoginThatHasNotUploaded()
	{
		var id = await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		A.Vault.Delete(id);
		await A.SyncAsync();
		A.Clock.Advance(TimeSpan.FromDays(31));

		Assert.Equal(1, A.Vault.TombstoneDue(A.Clock.Now()));

		Assert.Single(A.Vault.Unsent());
		Assert.Equal(0, A.Engine.Status.Unsent);
	}
}
