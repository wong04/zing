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

	// The listener's pipe while it waits, so Release can close it from the owning thread; 1 once released.
	private NamedPipeServerStream? _pipe;
	private int _released;

	private bool Released => Volatile.Read(ref _released) == 1;

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

	/// <summary>Calls <paramref name="onLaunch"/> with each later launch's command line until cancelled, or until the profile is released.</summary>
	public async Task ListenAsync(Action<IReadOnlyList<string>> onLaunch, Action<Exception> onBrokenMessage, CancellationToken cancel = default)
	{
		while (!Released)
		{
			NamedPipeServerStream pipe;
			try
			{
				pipe = new NamedPipeServerStream(PipeName(_key), PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
			}
			// The next owner already has the pipe's name.
			catch (IOException) when (Released)
			{
				return;
			}
			await using (pipe)
			{
				Interlocked.Exchange(ref _pipe, pipe);
				// Release may have run before the pipe was published, and so not closed it.
				if (Released) return;
				try
				{
					await pipe.WaitForConnectionAsync(cancel);
					onLaunch(await JsonSerializer.DeserializeAsync<string[]>(pipe, cancellationToken: cancel) ?? throw new JsonException("The message was null."));
				}
				catch (JsonException broken)
				{
					onBrokenMessage(broken);
				}
				catch (Exception closed) when (closed is ObjectDisposedException or IOException && Released)
				{
					return;
				}
			}
		}
	}

	/// <summary>
	/// Gives up the profile while this process goes on (quitting), so a launch from now on becomes the owner instead of
	/// being handed to a Zing that is closing. The listener stops first, so a launch that has already found this owner
	/// busy waits in HandOver for the next one's pipe. Call it on the thread that claimed the profile, which owns the mutex.
	/// </summary>
	public void Release()
	{
		if (Interlocked.Exchange(ref _released, 1) == 1) return;
		Interlocked.Exchange(ref _pipe, null)?.Dispose();
		_mutex.ReleaseMutex();
		_mutex.Dispose();
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
