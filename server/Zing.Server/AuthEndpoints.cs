namespace Zing.Server;

public sealed record GoogleRequest(string? IdToken);

public sealed record RefreshRequest(string? Refresh);

public sealed record LoginResponse(string Session, string Refresh, string UserId);

public sealed record RefreshResponse(string Session, string Refresh);

public static class AuthEndpoints
{
	public static void MapAuth(this IEndpointRouteBuilder app)
	{
		app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
		app.MapPost("/auth/google", SignIn);
		app.MapPost("/auth/refresh", Refresh);
		app.MapGet("/me", Me);
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

	private static IResult Me(HttpContext context, SessionTokens sessions)
	{
		if (!sessions.TryValidate(context, out var userId)) return Results.Unauthorized();
		return Results.Ok(new { userId });
	}
}
