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
