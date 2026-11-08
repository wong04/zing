using System.Text.Json;

namespace Zing.Server;

public sealed class RequestRejectedException(int status, string message) : Exception(message)
{
	public int Status { get; } = status;
}

public static class SyncLimits
{
	public const int MaxBlobBytes = 256 * 1024;
	public const int MaxBodyBytes = 4 * 1024 * 1024;
	public const int MaxItemsPerPush = 100;
	public const int MaxPageSize = 200;
	public const int MaxItemIdLength = 128;
	public static readonly TimeSpan TombstoneLifetime = TimeSpan.FromDays(30);
}

public static class SyncEndpoints
{
	private const string UserKey = "zing.userId";
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	public static void MapSync(this IEndpointRouteBuilder app)
	{
		var group = app.MapGroup("").AddEndpointFilter(RequireSession);
		group.MapKeys();
		group.MapManifest();
		group.MapChanges();
	}

	public static string UserId(HttpContext context) => (string)context.Items[UserKey]!;

	public static async Task<T> ReadBody<T>(HttpContext context) where T : class
	{
		if (context.Request.ContentLength > SyncLimits.MaxBodyBytes) throw TooLarge();

		using var buffer = new MemoryStream();
		var chunk = new byte[16 * 1024];
		int read;
		while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
		{
			if (buffer.Length + read > SyncLimits.MaxBodyBytes) throw TooLarge();
			buffer.Write(chunk, 0, read);
		}

		try
		{
			buffer.Position = 0;
			return await JsonSerializer.DeserializeAsync<T>(buffer, JsonOptions, context.RequestAborted)
				?? throw new RequestRejectedException(StatusCodes.Status400BadRequest, "A JSON body is required.");
		}
		catch (JsonException)
		{
			throw new RequestRejectedException(StatusCodes.Status400BadRequest, "The body is not valid JSON.");
		}
	}

	public static byte[] DecodeBlob(string? encoded, string field)
	{
		if (string.IsNullOrEmpty(encoded)) return [];
		if (encoded.Length > SyncLimits.MaxBlobBytes / 3 * 4 + 8) throw BlobTooLarge(field);

		var bytes = new byte[encoded.Length * 3 / 4 + 3];
		if (!Convert.TryFromBase64String(encoded, bytes, out var written))
			throw new RequestRejectedException(StatusCodes.Status400BadRequest, $"{field} is not valid base64.");
		if (written > SyncLimits.MaxBlobBytes) throw BlobTooLarge(field);
		return bytes[..written];
	}

	public static string EncodeBlob(byte[] blob) => Convert.ToBase64String(blob);

	private static async ValueTask<object?> RequireSession(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
	{
		var context = invocation.HttpContext;
		var sessions = context.RequestServices.GetRequiredService<SessionTokens>();
		if (!sessions.TryValidate(context, out var userId)) return Results.Unauthorized();
		context.Items[UserKey] = userId;

		try
		{
			return await next(invocation);
		}
		catch (RequestRejectedException rejected)
		{
			return Results.Json(new { error = rejected.Message }, statusCode: rejected.Status);
		}
	}

	private static RequestRejectedException TooLarge() =>
		new(StatusCodes.Status413PayloadTooLarge, "The request body is larger than 4 MiB.");

	private static RequestRejectedException BlobTooLarge(string field) =>
		new(StatusCodes.Status400BadRequest, $"{field} is larger than 256 KiB.");
}
