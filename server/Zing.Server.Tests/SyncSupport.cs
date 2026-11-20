using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Zing.Server.Tests;

public sealed record PushItem(string ItemId, long BaseRevision, string Summary = "c3VtbWFyeQ==", bool Tombstone = false, int Version = 1, string Password = "cGFzcw==", string History = "");

public sealed record PulledItem(string ItemId, int Version, long Revision, bool Tombstone, string Summary, string Password, string History);

public sealed record Pulled(List<PulledItem> Items, long Revision, long Horizon, bool More);

public sealed record Accepted(string ItemId, long Revision);

public sealed record Pushed(List<Accepted> Accepted, List<PulledItem> Conflicts);

public sealed record BlobBody(string? Blob, long Version);

public static class SyncSupport
{
	public static HttpClient SignedIn(this ServerFactory factory, string session)
	{
		var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session);
		return client;
	}

	public static async Task<HttpClient> SignedInClient(this ServerFactory factory, string sub = ServerFactory.AllowedSub)
	{
		var login = await factory.SignIn(factory.CreateClient(), sub);
		return factory.SignedIn(login.Session);
	}

	public static string SessionFor(this ServerFactory factory, string userId) =>
		factory.Services.GetRequiredService<SessionTokens>().Issue(userId);

	public static Task<HttpResponseMessage> Push(this HttpClient client, params PushItem[] items) =>
		client.PostAsJsonAsync("/changes", new { items }, ServerFactory.JsonOpts);

	public static Task<HttpResponseMessage> PushLibrary(this HttpClient client, params PushItem[] items) =>
		client.PostAsJsonAsync("/library/changes", new { items }, ServerFactory.JsonOpts);

	public static Task<Pushed> PushOk(this HttpClient client, params PushItem[] items) => Ok<Pushed>(client.Push(items));

	public static Task<Pushed> PushLibraryOk(this HttpClient client, params PushItem[] items) => Ok<Pushed>(client.PushLibrary(items));

	public static Task<Pulled> PullOk(this HttpClient client, long since = 0, int? limit = null) =>
		Ok<Pulled>(client.GetAsync($"/changes?since={since}" + (limit is null ? "" : $"&limit={limit}")));

	public static Task<Pulled> PullLibraryOk(this HttpClient client, long since = 0) =>
		Ok<Pulled>(client.GetAsync($"/library/changes?since={since}"));

	private static async Task<T> Ok<T>(Task<HttpResponseMessage> sent)
	{
		var response = await sent;
		response.EnsureSuccessStatusCode();
		return (await response.Content.ReadFromJsonAsync<T>(ServerFactory.JsonOpts))!;
	}

	public static string Base64OfSize(int bytes) => Convert.ToBase64String(new byte[bytes]);
}
