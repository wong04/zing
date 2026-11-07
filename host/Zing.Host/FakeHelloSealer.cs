using System.Security.Cryptography;
using System.Text;
using Zing.Core.Vault;

namespace Zing.Host;

/// <summary>
/// The Windows Hello stand-in for e2e runs, which must never show a prompt (AGENTS.md). Created only by <see cref="TestHooks.HelloSealer"/>,
/// so a real launch can't get it. Each call answers as the test set it with {kind: "test", action: "vault-hello", result: "pass" | "cancel" | "unavailable" | "reset"}; "reset" is Windows Hello's key being made again, so what was sealed before no longer opens.
/// It wraps the key under a fixed key: it stands in for Hello, and protects nothing.
/// </summary>
public sealed class FakeHelloSealer : IHelloSealer
{
	private const string WrapLabel = "e2e-fake-hello";

	private string _result = "pass";
	private int _generation;

	public event Action? AvailabilityChanged;

	public bool Available => _result != "unavailable";

	public void Answer(string result)
	{
		if (result is not ("pass" or "cancel" or "unavailable" or "reset")) throw new InvalidOperationException($"vault-hello takes pass, cancel, unavailable or reset, not {result}");
		if (result == "reset")
		{
			_generation++;
			return;
		}
		var wasAvailable = Available;
		_result = result;
		if (Available != wasAvailable) AvailabilityChanged?.Invoke();
	}

	public Task<byte[]> SealAsync(byte[] key)
	{
		Prompt();
		return Task.FromResult(KeyWrap.Wrap(CurrentKey(), key, WrapLabel));
	}

	public Task<byte[]> UnsealAsync(byte[] sealedBytes)
	{
		Prompt();
		return Task.FromResult(KeyWrap.Unwrap(CurrentKey(), sealedBytes, WrapLabel));
	}

	private byte[] CurrentKey() => SHA256.HashData(Encoding.UTF8.GetBytes($"zing e2e fake hello {_generation}"));

	private void Prompt()
	{
		if (_result == "cancel") throw new HelloCancelledException("The e2e test cancelled the Windows Hello prompt (vault-hello: cancel)");
		if (_result == "unavailable") throw new HelloUnavailableException("The e2e test has no Windows Hello (vault-hello: unavailable)");
	}
}
