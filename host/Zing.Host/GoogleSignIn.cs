using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Zing.Core.Sync;

namespace Zing.Host;

/// <summary>
/// The e2e stand-in for Google (AGENTS.md: no test reaches a real service): asks the local sync server's fake issuer
/// (ZING_SERVER_FAKE_GOOGLE=1, which Production refuses) for an ID token, with no window. Created only by <see cref="TestHooks.Sync"/>,
/// for the tests that don't test the sign-in window.
/// </summary>
public sealed class FakeIssuerSignIn(Uri server, string sub) : IGoogleSignIn
{
	public async Task<string> SignInAsync(CancellationToken cancellation)
	{
		using var http = new HttpClient { BaseAddress = server };
		using var response = await http.PostAsJsonAsync("fake-google/token", new { sub }, cancellation);
		if (!response.IsSuccessStatusCode) throw new GoogleSignInException($"The local server's fake issuer answered {(int)response.StatusCode}");
		var token = await response.Content.ReadFromJsonAsync<JsonElement>(cancellation);
		return token.GetProperty("idToken").GetString() ?? throw new GoogleSignInException("The fake issuer sent no ID token");
	}
}

/// <summary>What this build syncs with (Ruling 48), from its assembly metadata (Zing.Host.csproj): see <see cref="SyncBuildSettings"/>.</summary>
public static class SyncBuild
{
	/// <param name="app">What the sign-in window (SignInPopup) opens over.</param>
	public static SyncSetup? Setup(ZingApp app) =>
		SyncSetup.FromBuild(Metadata, (clientId, secret) => new GoogleSignIn(clientId, secret, GoogleEndpoints.Google, authorize => SignInPopup.OpenOver(app, authorize), Faults.Warning), Faults.Warning);

	private static string? Metadata(string key) =>
		typeof(SyncBuild).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().SingleOrDefault(a => a.Key == key)?.Value;
}
