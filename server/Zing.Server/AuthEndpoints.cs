namespace Zing.Server;

public sealed record GoogleRequest(string? IdToken);

public sealed record RefreshRequest(string? Refresh);

public sealed record DeleteAccountRequest(string? IdToken, string? UserId);

public sealed record LoginResponse(string Session, string Refresh, string UserId);

public sealed record RefreshResponse(string Session, string Refresh);

public static class AuthEndpoints
{
	/// <summary>How recent the Google sign-in behind a delete must be: one made for the delete, not a token kept from earlier.</summary>
	public static readonly TimeSpan DeleteSignInMaxAge = TimeSpan.FromMinutes(5);

	public static void MapAuth(this IEndpointRouteBuilder app)
	{
		app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
		app.MapPost("/auth/google", SignIn);
		app.MapPost("/auth/refresh", Refresh);
		app.MapPost("/auth/signout", SignOut);
		app.MapGet("/me", Me);
		app.MapPost("/account/delete", DeleteAccount);
	}

	private static async Task<IResult> SignIn(
		GoogleRequest? body,
		IGoogleTokenValidator google,
		ServerConfig config,
		Db db,
		SessionTokens sessions,
		CancellationToken cancellation)
	{
		if (string.IsNullOrEmpty(body?.IdToken)) return Results.BadRequest(new { error = "idToken is required." });

		GoogleIdentity identity;
		try
		{
			identity = await google.ValidateAsync(body.IdToken, cancellation);
		}
		catch (InvalidIdTokenException)
		{
			return Results.Unauthorized();
		}
		if (!config.AllowedSubs.Contains(identity.Sub)) return Results.StatusCode(StatusCodes.Status403Forbidden);

		var userId = db.FindOrCreateUser(identity.Sub);
		var refresh = Tokens.NewRefreshToken();
		db.StoreRefresh(Tokens.HashRefresh(refresh), userId);
		return Results.Ok(new LoginResponse(sessions.Issue(userId), refresh, userId));
	}

	private static IResult Refresh(RefreshRequest? body, Db db, ServerConfig config, SessionTokens sessions)
	{
		if (string.IsNullOrEmpty(body?.Refresh)) return Results.BadRequest(new { error = "refresh is required." });

		var next = Tokens.NewRefreshToken();
		var (outcome, userId) = db.RotateRefresh(Tokens.HashRefresh(body.Refresh), Tokens.HashRefresh(next), config.AllowedSubs);
		if (outcome == RefreshOutcome.Forbidden) return Results.StatusCode(StatusCodes.Status403Forbidden);
		if (outcome != RefreshOutcome.Rotated) return Results.Unauthorized();
		return Results.Ok(new RefreshResponse(sessions.Issue(userId!), next));
	}

	// Holding the token is the proof, so it needs no session (an expired one is the usual case). Every well-formed request gets
	// the same 204, so it says nothing about whether the token was known.
	private static IResult SignOut(RefreshRequest? body, Db db)
	{
		if (string.IsNullOrEmpty(body?.Refresh)) return Results.BadRequest(new { error = "refresh is required." });

		db.RevokeFamily(Tokens.HashRefresh(body.Refresh));
		return Results.NoContent();
	}

	// Deletes everything a Google account syncs, for a person who forgot their Zing password and has no recovery key. A session or
	// refresh token this PC kept isn't enough: the proof is a Google sign-in made just now, for this, validated as strictly as
	// /auth/google. The user it's asked for must be the one that sign-in maps to, so signing in with another Google account deletes
	// nothing (409). A user that is already gone has nothing left to delete (204), so a retry after a lost answer succeeds.
	private static async Task<IResult> DeleteAccount(
		DeleteAccountRequest? body,
		IGoogleTokenValidator google,
		ServerConfig config,
		Db db,
		TimeProvider clock,
		CancellationToken cancellation)
	{
		if (string.IsNullOrEmpty(body?.IdToken) || string.IsNullOrEmpty(body.UserId)) return Results.BadRequest(new { error = "idToken and userId are required." });

		GoogleIdentity identity;
		try
		{
			identity = await google.ValidateAsync(body.IdToken, cancellation);
		}
		catch (InvalidIdTokenException)
		{
			return Results.Unauthorized();
		}
		if (!config.AllowedSubs.Contains(identity.Sub)) return Results.StatusCode(StatusCodes.Status403Forbidden);
		if (clock.GetUtcNow() - identity.IssuedAt > DeleteSignInMaxAge) return Results.Unauthorized();

		return db.DeleteUser(identity.Sub, body.UserId) switch
		{
			DeleteOutcome.Deleted or DeleteOutcome.NoSuchUser => Results.NoContent(),
			DeleteOutcome.OtherUser => Results.Conflict(new { error = "That Google account isn't the one this sync account belongs to." }),
			var other => throw new InvalidOperationException($"Unknown delete outcome {other}"),
		};
	}

	private static IResult Me(HttpContext context, SessionTokens sessions)
	{
		if (!sessions.TryValidate(context, out var userId)) return Results.Unauthorized();
		return Results.Ok(new { userId });
	}
}
