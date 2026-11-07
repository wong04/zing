using System.Security.Cryptography;
using Zing.Core.Vault;

namespace Zing.Host;

/// <summary>
/// The Windows Hello stand-in for e2e runs, which must never show a prompt (AGENTS.md). Created only by <see cref="TestHooks.HelloSealer"/>,
/// so a real launch can't get it. Each call answers as the test set it with {kind: "test", action: "vault-hello", result: "pass" | "cancel" | "unavailable"}.
/// It wraps the key under a fixed key: it stands in for Hello, and protects nothing.
/// </summary>
public sealed class FakeHelloSealer : IHelloSealer
{
	private const string WrapLabel = "e2e-fake-hello";
	private static readonly byte[] FixedKey = SHA256.HashData("zing e2e fake hello"u8);

	private string _result = "pass";

	public event Action? AvailabilityChanged;

	public bool Available => _result != "unavailable";

	public void Answer(string result)
	{
		if (result is not ("pass" or "cancel" or "unavailable")) throw new InvalidOperationException($"vault-hello takes pass, cancel or unavailable, not {result}");
		var wasAvailable = Available;
		_result = result;
		if (Available != wasAvailable) AvailabilityChanged?.Invoke();
	}

	public Task<byte[]> SealAsync(byte[] key)
	{
		Prompt();
		return Task.FromResult(KeyWrap.Wrap(FixedKey, key, WrapLabel));
	}

	public Task<byte[]> UnsealAsync(byte[] sealedBytes)
	{
		Prompt();
		return Task.FromResult(KeyWrap.Unwrap(FixedKey, sealedBytes, WrapLabel));
	}

	private void Prompt()
	{
		if (_result == "cancel") throw new HelloCancelledException("The e2e test cancelled the Windows Hello prompt (vault-hello: cancel)");
		if (_result == "unavailable") throw new HelloUnavailableException("The e2e test has no Windows Hello (vault-hello: unavailable)");
	}
}
