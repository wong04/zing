# Zing sync server

An ASP.NET minimal API (.NET 9) that stores Zing's encrypted password vault. It only ever sees ciphertext. This guide deploys it to Railway (Hobby plan).

## Variables

| Variable | Required | What it is |
| --- | --- | --- |
| `ZING_GOOGLE_CLIENT_ID` | yes | The Client ID of the Google Desktop OAuth client (see below). The server only accepts ID tokens issued for this client. |
| `ZING_ALLOWED_SUBS` | yes | Comma-separated Google `sub` values allowed to sign in. Anyone else is refused. |
| `ZING_SESSION_KEY` | yes | Base64 of at least 32 random bytes. Signs the server's session tokens. |
| `ZING_DB_PATH` | no | SQLite file. Defaults to `/data/zing.db`, which is the volume mount. |
| `PORT` | no | Set by Railway. The image listens on it (8080 when unset). |

Generate the session key:

```
openssl rand -base64 32
```

Never set `ZING_SERVER_FAKE_GOOGLE` in production. It swaps Google token checks for a test issuer, and the server refuses to start with it in the Production environment. It exists for tests and CI only. With it on, `POST /fake-google/token` with `{"sub": "...", "email": "..."}` answers `{"idToken": "..."}` signed by the test issuer, which is how Zing's e2e runs sign in without Google; without it the endpoint doesn't exist.

Keep every value in Railway's Variables tab. Never put them in a file in the repo.

## Google OAuth client

Zing signs in with Google through the system browser and sends the ID token to this server, which checks its signature and audience. Use a Cloud project of your own for this.

1. Open https://console.cloud.google.com and create a project (for example `zing-sync`).
2. **APIs & Services > OAuth consent screen** (now under **Google Auth Platform**):
   - Press **Get started**. Pick an app name (for example `Zing`), your support email, audience **External**, and your contact email.
   - Under **Audience**, leave it in **Testing** and add your own Google account as a **Test user**.
   - Only the `openid` scope is used. It is non-sensitive and needs no review. Add no other scopes.
3. **Clients** (older UI: **APIs & Services > Credentials > Create credentials > OAuth client ID**):
   - Application type **Desktop app**, name `Zing`, press **Create**.
   - Copy the **Client ID**. This is `ZING_GOOGLE_CLIENT_ID`.
   - Google's token endpoint also wants the **Client secret** for Desktop clients, even with PKCE. Google documents it as not confidential for installed apps. The server does not need it; it belongs to the Zing app.
4. A Desktop client needs no registered redirect URI. Google accepts `http://127.0.0.1:<any port>/`.

While the app is in Testing, Google shows an "unverified app" warning on sign-in; continue past it.

## Finding your Google `sub`

The `sub` is a stable numeric ID for your Google account. It appears in the ID token and is what `ZING_ALLOWED_SUBS` matches.

- Run the C4 sign-in spike (`spikes/vault/google-signin`, steps in `spikes/vault/RUN-C4.md`) with your Desktop client. It prints `sub: <number>` after you sign in. Or:
- Decode the payload (the middle part) of any Google ID token issued to your account and read its `sub` claim.

Separate several `sub` values with commas to allow more than one account.

## Railway setup

1. In Railway, **New Project > Deploy from GitHub repo** and pick the Zing repo.
2. Open the service **Settings** and set **Root Directory** to `/server`. The Dockerfile's build context is `server/`. Railway then reads `server/railway.json`: the Dockerfile builder, the `/health` health check and an on-failure restart policy. (Instead of a root directory you can keep the repo root, point the config file at `/server/railway.json` and the Dockerfile path at `server/Dockerfile`, but the Dockerfile copies paths relative to `server/`, so the root directory route is the simple one.)
3. **Volume**: add a volume to the service and mount it at `/data`. `railway.json` cannot declare volumes, so this is a manual step. Without it the database is lost on every deploy.
4. **Variables**: add `ZING_GOOGLE_CLIENT_ID`, `ZING_ALLOWED_SUBS` and `ZING_SESSION_KEY`, plus `RAILWAY_RUN_UID=0`. Railway volumes are mounted root-owned, so the image's non-root user cannot write to `/data` unless the container runs as root.
5. **Networking**: generate a public domain, then deploy.
6. **Health check**: Railway calls `/health` (from `railway.json`) and only switches traffic when it returns 200. Check it yourself with `curl https://<your-domain>/health`.
7. **Usage limit**: in Railway's billing settings, set a usage limit and a usage alert so a runaway bill cannot happen on the Hobby plan.

## Sessions and refresh tokens

`POST /auth/google` starts a sign-in: a 15-minute session and a refresh token that rotates on each `POST /auth/refresh`. Reusing a rotated token revokes every refresh token of that user, except a repeat within 2 minutes of its first use while its successor is still unused: that is a client that lost the answer, so it gets a new pair and the successor it never saw is revoked. `POST /auth/signout` with `{"refresh": "..."}` revokes every token of that sign-in (and no other); it answers 204 whether or not the token was known.

## Network exposure

Reach the service only through Railway's proxy, and never expose its port directly (no TCP proxy, no public port mapping, nothing else in front of it). The per-IP rate limits trust the last `X-Forwarded-For` hop, which Railway's proxy appends. A client that can reach the port directly can forge that header and sidestep the limits.

## Local run and tests

```
dotnet test server/Zing.Server.Tests
```

To run the server locally without Google, set throwaway values in your shell (not in a file): `ZING_SERVER_FAKE_GOOGLE=1`, a generated `ZING_SESSION_KEY`, any `ZING_ALLOWED_SUBS`, and a `ZING_DB_PATH` in a temp folder. Then `dotnet run --project server/Zing.Server`.
