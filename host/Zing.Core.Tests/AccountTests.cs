using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zing.Core;

namespace Zing.Core.Tests;

/// <summary>Stands in for DPAPI: reversible, but the bytes on disk are not the input.</summary>
internal sealed class FakeCipher(bool available = true, bool foreign = false) : ISecretCipher
{
	public static readonly byte[] Mark = Encoding.ASCII.GetBytes("SEALED:");

	public bool IsAvailable { get; } = available;

	public byte[] Encrypt(byte[] plain) => [.. Mark, .. plain.Reverse()];

	public byte[] Decrypt(byte[] sealedBytes)
	{
		if (foreign || !sealedBytes.AsSpan().StartsWith(Mark)) throw new CryptographicException("not sealed by this key");
		return [.. sealedBytes.Skip(Mark.Length).Reverse()];
	}
}

public sealed class AccountTests : IDisposable
{
	private readonly List<string> _dirs = [];

	public void Dispose()
	{
		foreach (var dir in _dirs) Directory.Delete(dir, recursive: true);
	}

	private string TempDir()
	{
		var dir = Directory.CreateTempSubdirectory("zing-account-").FullName;
		_dirs.Add(dir);
		return dir;
	}

	private static AccountStore Store(string dir, ISecretCipher? cipher = null, bool startAsGuest = false) =>
		new(dir, cipher ?? new FakeCipher(), startAsGuest);

	private static AccountErrorCode? CodeOf(Action run)
	{
		try
		{
			run();
		}
		catch (AccountException error)
		{
			return error.Code;
		}
		return null;
	}

	[Fact]
	public void StartsWithNoAccountAndSignedOut()
	{
		var state = Store(TempDir()).State();
		Assert.False(state.HasAccount);
		Assert.False(state.SignedIn);
		Assert.Null(state.Profile);
	}

	[Fact]
	public void SignsUpThenSignsInAgainAfterARelaunchOnlyWithTheRightPassword()
	{
		var dir = TempDir();
		var first = Store(dir);
		first.SignUp("jdoe", "Jane Doe", "jane@example.com", "correct horse");
		Assert.True(first.State().HasAccount);
		Assert.True(first.State().SignedIn);

		var second = Store(dir);
		Assert.True(second.State().SignedIn);
		second.SignOut();
		Assert.Equal(AccountErrorCode.WrongPassword, CodeOf(() => second.SignIn("wrong password")));
		second.SignIn("correct horse");
		Assert.True(second.State().SignedIn);
	}

	[Fact]
	public void WritesThePasswordNowhereAndSealsItsCheck()
	{
		var dir = TempDir();
		const string password = "a-very-distinctive-password-7731";
		Store(dir).SignUp("jdoe", "Jane", "", password);
		var needle = Encoding.UTF8.GetBytes(password);
		var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
		Assert.NotEmpty(files);
		foreach (var file in files) Assert.False(File.ReadAllBytes(file).AsSpan().IndexOf(needle) >= 0, $"{file} contains the password");
		Assert.True(File.ReadAllBytes(Path.Combine(dir, "account.verifier")).AsSpan().StartsWith(FakeCipher.Mark));
	}

	[Fact]
	public void AsksForThePasswordAtLaunchWhenToldTo()
	{
		var dir = TempDir();
		var first = Store(dir);
		first.SignUp("jdoe", "Jane", "", "longenough1");
		first.SetRequirePasswordOnLaunch(true);
		var state = Store(dir).State();
		Assert.True(state.HasAccount);
		Assert.False(state.SignedIn);
		Assert.True(state.RequirePasswordOnLaunch);
	}

	public static TheoryData<string, string, string, AccountErrorCode> SignUpRejections()
	{
		var data = new TheoryData<string, string, string, AccountErrorCode>();
		foreach (var c in Vectors.Load("host/Zing.Core.Tests/vectors/account.vectors.json").GetProperty("signUpRejections").EnumerateArray())
		{
			var code = c.GetProperty("code").GetString()!;
			var parsed = Enum.GetValues<AccountErrorCode>().Single(e => e.Wire() == code);
			data.Add(c.GetProperty("name").GetString()!, c.GetProperty("username").GetString()!, c.GetProperty("password").GetString()!, parsed);
		}
		return data;
	}

	[Theory]
	[MemberData(nameof(SignUpRejections))]
	public void RejectsSignUp(string name, string username, string password, AccountErrorCode expected)
	{
		Assert.NotEmpty(name);
		Assert.Equal(expected, CodeOf(() => Store(TempDir()).SignUp(username, "", "", password)));
	}

	public static TheoryData<string, string, string, string> VerifierVectors()
	{
		var data = new TheoryData<string, string, string, string>();
		foreach (var c in Vectors.Load("host/Zing.Core.Tests/vectors/account.vectors.json").GetProperty("verifier").EnumerateArray())
			data.Add(c.GetProperty("name").GetString()!, c.GetProperty("password").GetString()!, c.GetProperty("saltHex").GetString()!, c.GetProperty("expectedHex").GetString()!);
		return data;
	}

	[Theory]
	[MemberData(nameof(VerifierVectors))]
	public void DerivesTheSameScryptCheckAsNode(string name, string password, string saltHex, string expectedHex)
	{
		Assert.NotEmpty(name);
		Assert.Equal(expectedHex, Convert.ToHexString(Scrypt.Derive(password, Convert.FromHexString(saltHex))).ToLowerInvariant());
	}

	[Theory]
	[InlineData("", "", 16, 1, 1, 64, "77d6576238657b203b19ca42c18a0497f16b4844e3074ae8dfdffa3fede21442fcd0069ded0948f8326a753a0fc81f17e8d3e0fb2e0d3628cf35e20c38d18906")]
	[InlineData("password", "NaCl", 1024, 8, 16, 64, "fdbabe1c9d3472007856e7190d01e9fe7c6ad7cbc8237830e77376634b3731622eaf30d92e22a3886ff109279d9830dac727afb94a83ee6d8360cbdfa2cc0640")]
	public void ScryptMatchesRfc7914(string password, string salt, int n, int r, int p, int length, string expected)
	{
		var actual = Scrypt.Compute(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(salt), n, r, p, length);
		Assert.Equal(expected, Convert.ToHexString(actual).ToLowerInvariant());
	}

	[Fact]
	public void RejectsASecondAccount()
	{
		var s = Store(TempDir());
		s.SignUp("jdoe", "", "", "longenough1");
		Assert.Equal(AccountErrorCode.AccountExists, CodeOf(() => s.SignUp("other", "", "", "longenough1")));
	}

	[Fact]
	public void RefusesToSignUpWhenTheKeyStoreIsUnavailableInsteadOfSavingUnprotected()
	{
		var dir = TempDir();
		var s = Store(dir, new FakeCipher(available: false));
		Assert.Equal(AccountErrorCode.EncryptionUnavailable, CodeOf(() => s.SignUp("jdoe", "", "", "longenough1")));
		Assert.False(s.State().HasAccount);
		Assert.Empty(Directory.GetFiles(dir));
	}

	[Fact]
	public void NeedsAPasswordResetWhenTheSealedCheckCannotBeOpened()
	{
		var dir = TempDir();
		Store(dir).SignUp("jdoe", "Jane", "", "longenough1");
		var moved = Store(dir, new FakeCipher(foreign: true));
		var state = moved.State();
		Assert.True(state.HasAccount);
		Assert.True(state.NeedsPasswordReset);
		Assert.False(state.SignedIn);

		var fresh = Store(dir);
		Assert.Equal(AccountErrorCode.ResetNotNeeded, CodeOf(() => fresh.ResetPassword("anotherlong1")));
	}

	[Fact]
	public void ResetsThePasswordOnTheSameProfileWhenTheCheckIsUnreadable()
	{
		var dir = TempDir();
		Store(dir).SignUp("jdoe", "Jane", "", "longenough1");
		File.Delete(Path.Combine(dir, "account.verifier"));
		var s = Store(dir);
		Assert.True(s.State().NeedsPasswordReset);
		Assert.Equal(AccountErrorCode.WeakPassword, CodeOf(() => s.ResetPassword("short")));
		s.ResetPassword("anotherlong1");
		Assert.False(s.State().NeedsPasswordReset);
		Assert.True(s.State().SignedIn);
		s.SignOut();
		s.SignIn("anotherlong1");
		Assert.Equal("jdoe", s.State().Profile!.Username);
	}

	[Fact]
	public void UpdatesTheProfileAndDeletingRemovesEveryFile()
	{
		var dir = TempDir();
		var s = Store(dir);
		s.SignUp("jdoe", "Jane", "", "longenough1");
		s.UpdateProfile("  Janet ", "j@example.com");
		Assert.Equal(new AccountProfile("jdoe", "Janet", "j@example.com"), s.State().Profile);
		s.DeleteAccount();
		Assert.Empty(Directory.GetFileSystemEntries(dir));
		Assert.False(s.State().HasAccount);
		Assert.False(s.State().SignedIn);
	}

	[Fact]
	public void CanStartAsAGuestForDevelopment() =>
		Assert.True(Store(TempDir(), startAsGuest: true).State().SignedIn);

	[Fact]
	public void ThrowsOnACorruptProfileInsteadOfPretendingThereIsNone()
	{
		var dir = TempDir();
		File.WriteAllText(Path.Combine(dir, "account.json"), "{ nope");
		Assert.Throws<AccountFileException>(() => Store(dir));
	}

	[Fact]
	public void ReadsTheProfileFileTheElectronBuildWrote()
	{
		var dir = TempDir();
		var salt = Convert.ToHexString(new byte[32]).ToLowerInvariant();
		File.WriteAllText(
			Path.Combine(dir, "account.json"),
			$"{{\n\t\"username\": \"jdoe\",\n\t\"displayName\": \"Jane\",\n\t\"email\": \"\",\n\t\"createdAt\": \"2026-10-31T00:00:00.000Z\",\n\t\"salt\": \"{salt}\",\n\t\"requirePassword\": true\n}}\n");
		var state = Store(dir).State();
		Assert.Equal(new AccountProfile("jdoe", "Jane", ""), state.Profile);
		Assert.True(state.RequirePasswordOnLaunch);
		Assert.True(state.NeedsPasswordReset);
	}

	[Fact]
	public void NotifiesSubscribersUntilTheyUnsubscribe()
	{
		var s = Store(TempDir());
		var seen = new List<bool>();
		var subscription = s.Subscribe(state => seen.Add(state.SignedIn));
		s.SignUp("jdoe", "", "", "longenough1");
		s.SignOut();
		subscription.Dispose();
		s.ContinueAsGuest();
		Assert.Equal([true, false], seen);
	}

	[Fact]
	public void RunTurnsABrokenRuleIntoARejectedOutcomeThatSerialisesLikeTheTs()
	{
		var s = Store(TempDir());
		var rejected = s.Run(() => s.SignIn("whatever"));
		Assert.Equal("""{"status":"rejected","code":"no-account","message":"No account yet. Create one first."}""", JsonSerializer.Serialize(rejected, ZingJson.Options));

		var ok = JsonSerializer.Serialize(s.Run(() => s.ContinueAsGuest()), ZingJson.Options);
		Assert.Equal("""{"status":"ok","state":{"hasAccount":false,"signedIn":true,"needsPasswordReset":false,"requirePasswordOnLaunch":false,"profile":null}}""", ok);
	}

	[Fact]
	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	public void DpapiRoundTripsAndNeverStoresThePlainBytes()
	{
		Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI is Windows-only");
		var cipher = new DpapiCipher();
		Assert.True(cipher.IsAvailable);
		var plain = Encoding.UTF8.GetBytes("secret-verifier-bytes-0123456789");
		var sealedBytes = cipher.Encrypt(plain);
		Assert.True(sealedBytes.AsSpan().IndexOf(plain) < 0);
		Assert.Equal(plain, cipher.Decrypt(sealedBytes));
		sealedBytes[^1] ^= 0xFF;
		Assert.Throws<CryptographicException>(() => cipher.Decrypt(sealedBytes));
	}

	[Fact]
	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	public void SignsUpAndInAgainWithRealDpapi()
	{
		Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI is Windows-only");
		var dir = TempDir();
		var cipher = new DpapiCipher();
		Store(dir, cipher).SignUp("jdoe", "Jane", "", "longenough1");
		var again = Store(dir, cipher);
		Assert.True(again.State().SignedIn);
		again.SignOut();
		again.SignIn("longenough1");
		Assert.Equal(AccountErrorCode.WrongPassword, CodeOf(() => again.SignIn("longenough2")));
	}
}
