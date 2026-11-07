using System.Runtime.Versioning;
using System.Security.Cryptography;
using Windows.Security.Credentials;
using Zing.Core.Vault;
using WinRtBuffer = Windows.Security.Cryptography.CryptographicBuffer;

namespace Zing.Host;

/// <summary>
/// The vault's secret key behind Windows Hello, by check C1's method (HelloKeySeal): the "Zing vault" Hello key, held by the TPM,
/// signs a fixed challenge; only Hello can make that signature, and HKDF turns it into the key that wraps the secret key.
/// An unpackaged app has no CoreWindow, so only the *ForWindow* calls (Windows 11 26100+) show a prompt; they need a visible owner window.
/// Every call is awaited, never waited on, so the UI thread keeps running while the prompt is up.
/// </summary>
public sealed class HelloSealer : IHelloSealer
{
	private readonly Func<nint?> _owner;
	private bool _available;

	public event Action? AvailabilityChanged;

	/// <param name="owner">The window the prompt belongs to: Zing's most recent window, or null with none open.</param>
	public HelloSealer(Func<nint?> owner)
	{
		_owner = owner;
		TestHooks.Background(CheckAsync(), "windows hello check");
	}

	public bool Available => _available;

	public async Task<byte[]> SealAsync(byte[] key)
	{
		if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) throw Unsupported();
		var credential = await OpenOrCreateAsync();
		var signature = await SignAsync(credential, HelloKeySeal.Challenge);
		try
		{
			return HelloKeySeal.Seal(signature, key);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(signature);
		}
	}

	public async Task<byte[]> UnsealAsync(byte[] sealedBytes)
	{
		if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) throw Unsupported();
		var challenge = HelloKeySeal.ChallengeOf(sealedBytes);
		var credential = await OpenAsync();
		var signature = await SignAsync(credential, challenge);
		try
		{
			return HelloKeySeal.Open(signature, sealedBytes);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(signature);
		}
	}

	private async Task<KeyCredential> OpenAsync()
	{
		await RequireSupportedAsync();
		var opened = await KeyCredentialManager.OpenAsync(HelloKeySeal.CredentialName);
		if (opened.Status == KeyCredentialStatus.Success) return opened.Credential;
		throw Failure("open the vault's Windows Hello key", opened.Status);
	}

	/// <summary>
	/// Another Zing profile for this Windows user may have made the key already. It is used, never replaced: the same key signs the
	/// same challenge the same way, so every vault sealed with it still opens.
	/// </summary>
	[SupportedOSPlatform("windows10.0.26100.0")]
	private async Task<KeyCredential> OpenOrCreateAsync()
	{
		await RequireSupportedAsync();
		var opened = await KeyCredentialManager.OpenAsync(HelloKeySeal.CredentialName);
		if (opened.Status == KeyCredentialStatus.Success) return opened.Credential;
		if (opened.Status != KeyCredentialStatus.NotFound) throw Failure("open the vault's Windows Hello key", opened.Status);
		var created = await KeyCredentialManager.RequestCreateForWindowAsync(Owner(), HelloKeySeal.CredentialName, KeyCredentialCreationOption.FailIfExists);
		if (created.Status == KeyCredentialStatus.Success) return created.Credential;
		throw Failure("create the vault's Windows Hello key", created.Status);
	}

	[SupportedOSPlatform("windows10.0.26100.0")]
	private async Task<byte[]> SignAsync(KeyCredential credential, byte[] challenge)
	{
		var signed = await credential.RequestSignForWindowAsync(Owner(), WinRtBuffer.CreateFromByteArray(challenge));
		if (signed.Status != KeyCredentialStatus.Success) throw Failure("sign with Windows Hello", signed.Status);
		WinRtBuffer.CopyToByteArray(signed.Result, out var signature);
		return signature;
	}

	private static Exception Failure(string what, KeyCredentialStatus status) => status switch
	{
		KeyCredentialStatus.UserCanceled or KeyCredentialStatus.UserPrefersPassword => new HelloCancelledException($"Windows Hello was cancelled ({status})"),
		_ => new HelloUnavailableException($"Couldn't {what}: Windows Hello answered {status}"),
	};

	private static HelloUnavailableException Unsupported() => new("The password vault needs Windows 11 24H2 or later for its Windows Hello prompt");

	[SupportedOSPlatform("windows10.0.26100.0")]
	private Windows.UI.WindowId Owner()
	{
		var hwnd = _owner() ?? throw new HelloUnavailableException("No Zing window is open to show the Windows Hello prompt");
		// A hidden owner shows no prompt at all (check C1), which would look like a hang.
		if (!Native.IsWindowVisible(hwnd)) throw new HelloUnavailableException("The Zing window is hidden, so Windows Hello can't show its prompt");
		return new Windows.UI.WindowId { Value = (ulong)hwnd };
	}

	private async Task RequireSupportedAsync()
	{
		var supported = await IsSupportedAsync();
		SetAvailable(supported);
		if (!supported) throw new HelloUnavailableException("Windows Hello isn't set up on this PC");
	}

	/// <summary>No prompt: whether a Hello PIN, face or fingerprint is set up for this Windows user.</summary>
	private static async Task<bool> IsSupportedAsync() => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) && await KeyCredentialManager.IsSupportedAsync();

	private async Task CheckAsync() => SetAvailable(await IsSupportedAsync());

	private void SetAvailable(bool available)
	{
		if (available == _available) return;
		_available = available;
		AvailabilityChanged?.Invoke();
	}
}
