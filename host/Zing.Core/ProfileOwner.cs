using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace Zing.Core;

/// <summary>
/// Which process owns a profile, and how a later launch reaches it. A named mutex decides the owner:
/// Windows releases it when the process ends, however it ends, so a dead owner leaves nothing behind.
/// A named pipe carries a later launch's command line, in this session and for this user only.
/// </summary>
public sealed class ProfileOwner
{
	private readonly string _key;

	// Held, not read: closing the mutex would give up the profile.
	private readonly Mutex _mutex;

	private ProfileOwner(string key, Mutex mutex)
	{
		_key = key;
		_mutex = mutex;
	}

	/// <summary>The profile's owner if nothing else holds it, otherwise null. The caller keeps it for as long as the process runs.</summary>
	public static ProfileOwner? TryClaim(string key)
	{
		var mutex = new Mutex(false, @"Local\" + key);
		try
		{
			if (mutex.WaitOne(0)) return new ProfileOwner(key, mutex);
		}
		catch (AbandonedMutexException)
		{
			// The last owner died holding it, and this thread holds it now.
			return new ProfileOwner(key, mutex);
		}
		mutex.Dispose();
		return null;
	}

	/// <summary>Calls <paramref name="onLaunch"/> with each later launch's command line until cancelled.</summary>
	public async Task ListenAsync(Action<IReadOnlyList<string>> onLaunch, Action<Exception> onBrokenMessage, CancellationToken cancel = default)
	{
		while (true)
		{
			await using var pipe = new NamedPipeServerStream(PipeName(_key), PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
			await pipe.WaitForConnectionAsync(cancel);
			try
			{
				onLaunch(await JsonSerializer.DeserializeAsync<string[]>(pipe, cancellationToken: cancel) ?? throw new JsonException("The message was null."));
			}
			catch (JsonException broken)
			{
				onBrokenMessage(broken);
			}
		}
	}

	/// <summary>Gives this launch's command line to the owner, waiting for it to reach ListenAsync. Throws TimeoutException if it never does.</summary>
	public static void HandOver(string key, IReadOnlyList<string> argv, TimeSpan patience)
	{
		using var pipe = Connect(key, patience);
		JsonSerializer.Serialize(pipe, argv);
	}

	public static NamedPipeClientStream Connect(string key, TimeSpan patience)
	{
		var pipe = new NamedPipeClientStream(".", PipeName(key), PipeDirection.Out, PipeOptions.CurrentUserOnly);
		pipe.Connect(patience);
		return pipe;
	}

	// Unlike the mutex's Local\ name, a pipe's name is machine-wide, so it says which session it serves.
	private static string PipeName(string key) => $"{key}-{Process.GetCurrentProcess().SessionId}";
}
