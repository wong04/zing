using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Zing.Core;

public enum AccountErrorCode
{
	AccountExists,
	WeakPassword,
	EmptyField,
	WrongPassword,
	NoAccount,
	EncryptionUnavailable,
	ResetNotNeeded,
}

public static class AccountErrorCodes
{
	/// <summary>The kebab-case code the renderer switches on (src/shared/shell/shellState.ts ACCOUNT_ERROR_CODES).</summary>
	public static string Wire(this AccountErrorCode code) => code switch
	{
		AccountErrorCode.AccountExists => "account-exists",
		AccountErrorCode.WeakPassword => "weak-password",
		AccountErrorCode.EmptyField => "empty-field",
		AccountErrorCode.WrongPassword => "wrong-password",
		AccountErrorCode.NoAccount => "no-account",
		AccountErrorCode.EncryptionUnavailable => "encryption-unavailable",
		AccountErrorCode.ResetNotNeeded => "reset-not-needed",
		_ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
	};

	public static string Message(this AccountErrorCode code) => code switch
	{
		AccountErrorCode.AccountExists => "An account already exists on this computer.",
		AccountErrorCode.WeakPassword => $"Use at least {AccountStore.MinPasswordLength} characters.",
		AccountErrorCode.EmptyField => "Fill in every field.",
		AccountErrorCode.WrongPassword => "That password doesn't match.",
		AccountErrorCode.NoAccount => "No account yet. Create one first.",
		AccountErrorCode.EncryptionUnavailable => "This computer can't protect a saved password (the Windows key store is unavailable).",
		AccountErrorCode.ResetNotNeeded => "Your password is already set.",
		_ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
	};
}

/// <summary>A rule of the account gate was broken (wrong password, weak password, ...). The code is what the UI shows.</summary>
public sealed class AccountException(AccountErrorCode code) : Exception(code.Message())
{
	public AccountErrorCode Code { get; } = code;
}

/// <summary>An account file exists but can't be read or isn't a valid account (src/main/account.ts AccountFileError).</summary>
public sealed class AccountFileException(string path, Exception inner)
	: Exception($"Could not read the account at {path}: {inner.Message}", inner)
{
	public string Path { get; } = path;
}

/// <summary>The OS key store, injected so the rules here can be tested without Windows.</summary>
public interface ISecretCipher
{
	bool IsAvailable { get; }

	byte[] Encrypt(byte[] plain);

	/// <summary>Throws <see cref="CryptographicException"/> when the bytes were sealed by another user or machine.</summary>
	byte[] Decrypt(byte[] sealedBytes);
}

public sealed record AccountProfile(string Username, string DisplayName, string Email);

public sealed record AccountState(
	bool HasAccount,
	bool SignedIn,
	bool NeedsPasswordReset,
	bool RequirePasswordOnLaunch,
	// The renderer expects an explicit null here, not an absent key.
	[property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] AccountProfile? Profile);

/// <summary>`{status:'ok', state}` or `{status:'rejected', code, message}` (src/shared/shell/shellState.ts AccountOutcome).</summary>
public sealed record AccountOutcome(string Status, AccountState? State = null, string? Code = null, string? Message = null)
{
	public static AccountOutcome Ok(AccountState state) => new("ok", State: state);

	public static AccountOutcome Rejected(AccountErrorCode code) => new("rejected", Code: code.Wire(), Message: code.Message());
}

/// <summary>
/// A local account that gates the app on this computer: nothing is uploaded.
/// The profile is a plain JSON file (nothing in it is secret). The password
/// check is a scrypt hash, sealed with the OS key store before it touches disk;
/// the password itself is never written or logged. Same files and scrypt
/// parameters as src/main/account.ts. Not thread-safe: call from the UI thread.
/// </summary>
public sealed class AccountStore
{
	public const int MinPasswordLength = 8;

	private const string ProfileFile = "account.json";
	private const string VerifierFile = "account.verifier";
	private const int SaltBytes = 32;

	private readonly string _dir;
	private readonly ISecretCipher _cipher;
	private readonly List<Action<AccountState>> _listeners = [];
	private StoredAccount? _account;
	private bool _signedIn;

	/// <param name="startAsGuest">Starts signed in as a guest. Only for development and tests.</param>
	public AccountStore(string dir, ISecretCipher cipher, bool startAsGuest = false)
	{
		_dir = dir;
		_cipher = cipher;
		_account = Load();
		// A returning user is signed in automatically unless they asked for a lock.
		var unlocked = _account is { RequirePassword: false } && !NeedsReset();
		_signedIn = startAsGuest || unlocked;
	}

	public AccountState State()
	{
		var account = _account;
		return new AccountState(
			HasAccount: account is not null,
			SignedIn: _signedIn,
			NeedsPasswordReset: NeedsReset(),
			RequirePasswordOnLaunch: account?.RequirePassword ?? false,
			Profile: account is null ? null : new AccountProfile(account.Username, account.DisplayName, account.Email));
	}

	public IDisposable Subscribe(Action<AccountState> listener)
	{
		_listeners.Add(listener);
		return new Unsubscriber(() => _listeners.Remove(listener));
	}

	public void SignUp(string username, string displayName, string email, string password)
	{
		if (_account is not null) Fail(AccountErrorCode.AccountExists);
		var user = username.Trim();
		if (user == "" || password == "") Fail(AccountErrorCode.EmptyField);
		if (password.Length < MinPasswordLength) Fail(AccountErrorCode.WeakPassword);
		if (!_cipher.IsAvailable) Fail(AccountErrorCode.EncryptionUnavailable);

		var salt = RandomNumberGenerator.GetBytes(SaltBytes);
		StoreVerifier(password, salt);
		Save(new StoredAccount(
			user,
			displayName.Trim() is { Length: > 0 } name ? name : user,
			email.Trim(),
			DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
			Convert.ToHexString(salt).ToLowerInvariant(),
			RequirePassword: false));
		_signedIn = true;
		Emit();
	}

	public void SignIn(string password)
	{
		var account = _account ?? throw new AccountException(AccountErrorCode.NoAccount);
		var stored = ReadVerifier() ?? throw new AccountException(AccountErrorCode.NoAccount);
		var candidate = Scrypt.Derive(password, Convert.FromHexString(account.Salt));
		if (!CryptographicOperations.FixedTimeEquals(candidate, stored)) Fail(AccountErrorCode.WrongPassword);
		_signedIn = true;
		Emit();
	}

	/// <summary>For when the stored check can't be read; sets a new password on the same profile.</summary>
	public void ResetPassword(string password)
	{
		var account = _account ?? throw new AccountException(AccountErrorCode.NoAccount);
		if (!NeedsReset()) Fail(AccountErrorCode.ResetNotNeeded);
		if (password.Length < MinPasswordLength) Fail(AccountErrorCode.WeakPassword);
		if (!_cipher.IsAvailable) Fail(AccountErrorCode.EncryptionUnavailable);
		StoreVerifier(password, Convert.FromHexString(account.Salt));
		_signedIn = true;
		Emit();
	}

	public void SignOut()
	{
		_signedIn = false;
		Emit();
	}

	public void ContinueAsGuest()
	{
		_signedIn = true;
		Emit();
	}

	public void UpdateProfile(string displayName, string email)
	{
		var account = _account ?? throw new AccountException(AccountErrorCode.NoAccount);
		Save(account with { DisplayName = displayName.Trim() is { Length: > 0 } name ? name : account.Username, Email = email.Trim() });
		Emit();
	}

	public void SetRequirePasswordOnLaunch(bool required)
	{
		var account = _account ?? throw new AccountException(AccountErrorCode.NoAccount);
		Save(account with { RequirePassword = required });
		Emit();
	}

	public void DeleteAccount()
	{
		File.Delete(Path.Combine(_dir, ProfileFile));
		File.Delete(Path.Combine(_dir, VerifierFile));
		_account = null;
		_signedIn = false;
		Emit();
	}

	/// <summary>Runs one account operation as the bridge channels do: a broken rule becomes a 'rejected' outcome, anything else still throws.</summary>
	public AccountOutcome Run(Action operation)
	{
		try
		{
			operation();
		}
		catch (AccountException error)
		{
			return AccountOutcome.Rejected(error.Code);
		}
		return AccountOutcome.Ok(State());
	}

	private static void Fail(AccountErrorCode code) => throw new AccountException(code);

	private void StoreVerifier(string password, byte[] salt)
	{
		Directory.CreateDirectory(_dir);
		WriteAtomic(VerifierFile, _cipher.Encrypt(Scrypt.Derive(password, salt)));
	}

	private byte[]? ReadVerifier()
	{
		var path = Path.Combine(_dir, VerifierFile);
		byte[] sealedBytes;
		try
		{
			sealedBytes = File.ReadAllBytes(path);
		}
		catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
		{
			return null;
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			throw new AccountFileException(path, error);
		}
		try
		{
			return _cipher.Decrypt(sealedBytes);
		}
		catch (CryptographicException)
		{
			// A different Windows user or machine can't open it; the reset flow handles that.
			return null;
		}
	}

	private bool NeedsReset() => _account is not null && ReadVerifier() is null;

	private StoredAccount? Load()
	{
		var path = Path.Combine(_dir, ProfileFile);
		string raw;
		try
		{
			raw = File.ReadAllText(path, Encoding.UTF8);
		}
		catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
		{
			return null;
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			throw new AccountFileException(path, error);
		}
		try
		{
			return StoredAccount.Parse(JsonNode.Parse(raw));
		}
		catch (Exception error) when (error is JsonException or InvalidDataException)
		{
			throw new AccountFileException(path, error);
		}
	}

	private void Save(StoredAccount account)
	{
		JsonFiles.WriteJsonAtomic(Path.Combine(_dir, ProfileFile), account);
		_account = account;
	}

	private void WriteAtomic(string name, byte[] data)
	{
		var path = Path.Combine(_dir, name);
		var temp = $"{path}.{Environment.ProcessId}.tmp";
		File.WriteAllBytes(temp, data);
		File.Move(temp, path, overwrite: true);
	}

	private void Emit()
	{
		var state = State();
		foreach (var listener in _listeners.ToArray()) listener(state);
	}

	private sealed class Unsubscriber(Action remove) : IDisposable
	{
		public void Dispose() => remove();
	}

	/// <summary>The profile file's shape. Property order is the file's key order, as in the TS.</summary>
	private sealed record StoredAccount(string Username, string DisplayName, string Email, string CreatedAt, string Salt, bool RequirePassword)
	{
		public static StoredAccount Parse(JsonNode? node)
		{
			if (node is not JsonObject obj) throw new InvalidDataException("not an object");
			return new StoredAccount(
				Text(obj, "username"),
				Text(obj, "displayName"),
				Text(obj, "email"),
				Text(obj, "createdAt"),
				Text(obj, "salt"),
				obj["requirePassword"] is JsonValue flag && flag.TryGetValue<bool>(out var required)
					? required
					: throw new InvalidDataException("requirePassword must be a boolean"));
		}

		private static string Text(JsonObject obj, string key) =>
			obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw new InvalidDataException($"{key} must be a string");
	}
}

/// <summary>
/// scrypt (RFC 7914), which .NET has no built-in for. Parameters match node's
/// scryptSync defaults (N=16384, r=8, p=1, 32-byte key) so an Electron-made
/// salt yields the same check bytes; the shared vectors in
/// src/main/account.vectors.json hold the two to each other.
/// </summary>
public static class Scrypt
{
	private const int N = 16384;
	private const int R = 8;
	private const int P = 1;
	private const int KeyBytes = 32;

	/// <summary>The account password check: UTF-8 password, node's default scrypt cost.</summary>
	public static byte[] Derive(string password, byte[] salt) =>
		Compute(Encoding.UTF8.GetBytes(password), salt, N, R, P, KeyBytes);

	public static byte[] Compute(byte[] password, byte[] salt, int n, int r, int p, int length)
	{
		var blockBytes = 128 * r;
		var blockWords = 32 * r;
		var b = Rfc2898DeriveBytes.Pbkdf2(password, salt, 1, HashAlgorithmName.SHA256, p * blockBytes);
		var x = new uint[blockWords];
		var y = new uint[blockWords];
		var v = new uint[n * blockWords];
		for (var i = 0; i < p; i++)
		{
			var chunk = b.AsSpan(i * blockBytes, blockBytes);
			for (var w = 0; w < blockWords; w++) x[w] = BinaryPrimitives.ReadUInt32LittleEndian(chunk[(w * 4)..]);
			for (var j = 0; j < n; j++)
			{
				x.CopyTo(v.AsSpan(j * blockWords));
				BlockMix(x, y, r);
			}
			for (var j = 0; j < n; j++)
			{
				var k = (int)(x[(2 * r - 1) * 16] & (uint)(n - 1));
				var from = v.AsSpan(k * blockWords, blockWords);
				for (var w = 0; w < blockWords; w++) x[w] ^= from[w];
				BlockMix(x, y, r);
			}
			for (var w = 0; w < blockWords; w++) BinaryPrimitives.WriteUInt32LittleEndian(chunk[(w * 4)..], x[w]);
		}
		return Rfc2898DeriveBytes.Pbkdf2(password, b, 1, HashAlgorithmName.SHA256, length);
	}

	private static void BlockMix(uint[] x, uint[] y, int r)
	{
		Span<uint> t = stackalloc uint[16];
		x.AsSpan((2 * r - 1) * 16, 16).CopyTo(t);
		for (var i = 0; i < 2 * r; i++)
		{
			for (var k = 0; k < 16; k++) t[k] ^= x[i * 16 + k];
			Salsa20_8(t);
			t.CopyTo(y.AsSpan((i % 2 == 0 ? i / 2 : r + i / 2) * 16, 16));
		}
		y.CopyTo(x, 0);
	}

	private static void Salsa20_8(Span<uint> b)
	{
		Span<uint> x = stackalloc uint[16];
		b.CopyTo(x);
		for (var round = 0; round < 8; round += 2)
		{
			x[4] ^= Rotl(x[0] + x[12], 7); x[8] ^= Rotl(x[4] + x[0], 9);
			x[12] ^= Rotl(x[8] + x[4], 13); x[0] ^= Rotl(x[12] + x[8], 18);
			x[9] ^= Rotl(x[5] + x[1], 7); x[13] ^= Rotl(x[9] + x[5], 9);
			x[1] ^= Rotl(x[13] + x[9], 13); x[5] ^= Rotl(x[1] + x[13], 18);
			x[14] ^= Rotl(x[10] + x[6], 7); x[2] ^= Rotl(x[14] + x[10], 9);
			x[6] ^= Rotl(x[2] + x[14], 13); x[10] ^= Rotl(x[6] + x[2], 18);
			x[3] ^= Rotl(x[15] + x[11], 7); x[7] ^= Rotl(x[3] + x[15], 9);
			x[11] ^= Rotl(x[7] + x[3], 13); x[15] ^= Rotl(x[11] + x[7], 18);
			x[1] ^= Rotl(x[0] + x[3], 7); x[2] ^= Rotl(x[1] + x[0], 9);
			x[3] ^= Rotl(x[2] + x[1], 13); x[0] ^= Rotl(x[3] + x[2], 18);
			x[6] ^= Rotl(x[5] + x[4], 7); x[7] ^= Rotl(x[6] + x[5], 9);
			x[4] ^= Rotl(x[7] + x[6], 13); x[5] ^= Rotl(x[4] + x[7], 18);
			x[11] ^= Rotl(x[10] + x[9], 7); x[8] ^= Rotl(x[11] + x[10], 9);
			x[9] ^= Rotl(x[8] + x[11], 13); x[10] ^= Rotl(x[9] + x[8], 18);
			x[12] ^= Rotl(x[15] + x[14], 7); x[13] ^= Rotl(x[12] + x[15], 9);
			x[14] ^= Rotl(x[13] + x[12], 13); x[15] ^= Rotl(x[14] + x[13], 18);
		}
		for (var i = 0; i < 16; i++) b[i] += x[i];
	}

	private static uint Rotl(uint value, int bits) => (value << bits) | (value >> (32 - bits));
}
