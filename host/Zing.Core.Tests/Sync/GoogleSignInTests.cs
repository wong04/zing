using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Zing.Core.Sync;

namespace Zing.Core.Tests.Sync;

/// <summary>Google sign-in in its window, driven without WebView2: the window is <see cref="FakeSignInPopup"/>, Google is <see cref="FakeGoogle"/>.</summary>
public sealed class GoogleSignInTests : IDisposable
{
	private const string IdToken = "an-id-token";
	private readonly FakeGoogle _google = new(() => Task.FromResult(IdToken));

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	public void Dispose() => _google.Dispose();

	private static async Task WaitUntil(Func<bool> done, string what)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
		timeout.CancelAfter(TimeSpan.FromSeconds(10));
		while (!done()) await Task.Delay(20, timeout.Token).ContinueWith(t => t.IsCanceled ? throw new TimeoutException(what) : t, TaskScheduler.Default).Unwrap();
	}

	private static bool Refuses(int port)
	{
		using var client = new TcpClient();
		try
		{
			client.Connect(IPAddress.Loopback, port);
			return false;
		}
		catch (SocketException)
		{
			return true;
		}
	}

	[Fact]
	public async Task OneWindowOpensAtGooglesAuthorizeAddressWithPkceAndAState()
	{
		var signIn = _google.SignIn().SignInAsync(Ct);

		var popup = Assert.Single(_google.Popups);
		Assert.StartsWith(_google.Endpoints.Authorize + "?", popup.Authorize.AbsoluteUri);
		Assert.Equal("test-client", popup.Query("client_id"));
		Assert.Equal("code", popup.Query("response_type"));
		Assert.Equal("openid email", popup.Query("scope"));
		Assert.Equal("S256", popup.Query("code_challenge_method"));
		Assert.Matches(@"^http://127\.0\.0\.1:\d+/$", popup.Query("redirect_uri"));
		Assert.NotEmpty(popup.Query("state"));
		popup.CloseAsThePersonWould();
		await Assert.ThrowsAsync<SignInWindowClosedException>(() => signIn);
	}

	[Fact]
	public async Task TheRedirectCompletesTheSignInAndClosesTheWindow()
	{
		var signIn = _google.SignIn().SignInAsync(Ct);
		var popup = Assert.Single(_google.Popups);

		Assert.Equal(HttpStatusCode.OK, await popup.RedirectAsync($"code=the-code&state={popup.Query("state")}"));

		Assert.Equal(IdToken, await signIn);
		await popup.Closed.WaitAsync(TimeSpan.FromSeconds(10), Ct);
		Assert.True(popup.ClosedBySignIn);
		var form = Assert.Single(_google.TokenRequests);
		Assert.Equal("the-code", form["code"]);
		Assert.Equal("authorization_code", form["grant_type"]);
		Assert.Equal(popup.Query("redirect_uri"), form["redirect_uri"]);
		Assert.Equal("test-secret", form["client_secret"]);
		var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
		Assert.Equal(popup.Query("code_challenge"), challenge);
	}

	[Fact]
	public async Task ARedirectWithAnotherStateIsIgnoredAndTheWindowStaysOpen()
	{
		var signIn = _google.SignIn().SignInAsync(Ct);
		var popup = Assert.Single(_google.Popups);

		Assert.Equal(HttpStatusCode.BadRequest, await popup.RedirectAsync("code=planted&state=not-this-sign-in"));

		Assert.False(signIn.IsCompleted);
		Assert.False(popup.Closed.IsCompleted);
		Assert.Equal(HttpStatusCode.OK, await popup.RedirectAsync($"code=the-code&state={popup.Query("state")}"));
		Assert.Equal(IdToken, await signIn);
		Assert.Equal("the-code", Assert.Single(_google.TokenRequests)["code"]);
	}

	[Fact]
	public async Task ClosingTheWindowBeforeTheRedirectCancelsTheSignInAndStopsListening()
	{
		var signIn = _google.SignIn().SignInAsync(Ct);
		var popup = Assert.Single(_google.Popups);
		var port = new Uri(popup.Query("redirect_uri")).Port;

		popup.CloseAsThePersonWould();

		var cancelled = await Assert.ThrowsAsync<SignInWindowClosedException>(() => signIn);
		Assert.IsAssignableFrom<OperationCanceledException>(cancelled);
		await WaitUntil(() => Refuses(port), "the loopback port to close");
		Assert.Empty(_google.TokenRequests);
	}

	[Fact]
	public async Task GooglesRefusalEndsTheSignInWithItsReasonAndClosesTheWindow()
	{
		var signIn = _google.SignIn().SignInAsync(Ct);
		var popup = Assert.Single(_google.Popups);

		await popup.RedirectAsync($"error=access_denied&state={popup.Query("state")}");

		var refused = await Assert.ThrowsAsync<GoogleSignInException>(() => signIn);
		Assert.Equal("Google sign-in was cancelled.", refused.Message);
		await popup.Closed.WaitAsync(TimeSpan.FromSeconds(10), Ct);
		Assert.True(popup.ClosedBySignIn);
	}

	[Fact]
	public async Task CancellingTheSignInClosesItsWindow()
	{
		using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
		var signIn = _google.SignIn().SignInAsync(cancel.Token);
		var popup = Assert.Single(_google.Popups);

		cancel.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signIn);
		await popup.Closed.WaitAsync(TimeSpan.FromSeconds(10), Ct);
		Assert.True(popup.ClosedBySignIn);
	}

	[Fact]
	public async Task ASecondSignInBringsTheOpenWindowToTheFrontAndTakesItOver()
	{
		var google = _google.SignIn();
		var first = google.SignInAsync(Ct);
		var second = google.SignInAsync(Ct);

		var popup = Assert.Single(_google.Popups);
		Assert.Equal(1, popup.BroughtToFront);
		await Assert.ThrowsAsync<SignInTakenOverException>(() => first);
		Assert.False(popup.Closed.IsCompleted);

		await popup.RedirectAsync($"code=the-code&state={popup.Query("state")}");
		Assert.Equal(IdToken, await second);
		Assert.Single(_google.TokenRequests);
	}

	[Fact]
	public async Task TheSignInTakenOverBeingCancelledLeavesTheWindowToTheNewerOne()
	{
		var google = _google.SignIn();
		using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
		var first = google.SignInAsync(cancel.Token);

		// As a window's Settings does when the person starts again: the new sign-in first, then Cancel for the old one.
		var second = google.SignInAsync(Ct);
		cancel.Cancel();

		await Assert.ThrowsAsync<SignInTakenOverException>(() => first);
		var popup = Assert.Single(_google.Popups);
		Assert.False(popup.Closed.IsCompleted);
		await popup.RedirectAsync($"code=the-code&state={popup.Query("state")}");
		Assert.Equal(IdToken, await second);
	}

	[Fact]
	public async Task AWindowThatCantShowThePageEndsTheSignInWithAnErrorNotACancellation()
	{
		var signIn = _google.SignIn().SignInAsync(Ct);
		var popup = Assert.Single(_google.Popups);
		var port = new Uri(popup.Query("redirect_uri")).Port;

		popup.FailToStart();

		var failed = await Assert.ThrowsAsync<SignInWindowFailedException>(() => signIn);
		Assert.IsNotAssignableFrom<OperationCanceledException>(failed);
		Assert.Equal("Zing couldn't open the Google sign-in window. Try turning on sync again.", failed.Message);
		await WaitUntil(() => Refuses(port), "the loopback port to close");
	}

	[Fact]
	public async Task ASignInWhoseWindowFailedToOpenLeavesTheNextOneToOpenItsOwn()
	{
		var google = _google.SignIn();
		_google.FailingOpens = 1;

		await Assert.ThrowsAsync<InvalidOperationException>(() => google.SignInAsync(Ct));
		var second = google.SignInAsync(Ct);

		var popup = Assert.Single(_google.Popups);
		Assert.Equal(0, popup.BroughtToFront);
		await popup.RedirectAsync($"code=the-code&state={popup.Query("state")}");
		Assert.Equal(IdToken, await second);
	}

	[Fact]
	public async Task AfterAWindowClosesTheNextSignInOpensANewOne()
	{
		var google = _google.SignIn();
		var first = google.SignInAsync(Ct);
		_google.Popups[0].CloseAsThePersonWould();
		await Assert.ThrowsAsync<SignInWindowClosedException>(() => first);

		var second = google.SignInAsync(Ct);

		Assert.Equal(2, _google.Popups.Count);
		var popup = _google.Popups[1];
		await popup.RedirectAsync($"code=the-code&state={popup.Query("state")}");
		Assert.Equal(IdToken, await second);
	}
}
