using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// One Zing per profile (single-instance.ts). A second launch hands its
/// command line to the running instance and exits. The key is the
/// profile folder, so parallel e2e runs never find each other or the user's Zing.
/// </summary>
public static class SingleInstance
{
	/// <summary>Long enough for an owner to reach Listen, which comes after loading its profile, on a slow PC.</summary>
	private static readonly TimeSpan OwnerPatience = TimeSpan.FromSeconds(30);

	/// <summary>Names this profile's pipes: the owner's, and the AI one zing-mcp.exe is told with --profile.</summary>
	public static string ProfileKey => "Zing-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.UserData.ToLowerInvariant())))[..16];

	// A static, so the collector can't close the mutex while this process runs.
	private static ProfileOwner? _owner;

	/// <summary>
	/// True when another Zing owns this profile; the caller exits. It has been handed this launch, or, when it never
	/// answered (still starting, or stuck on an error box), the person has been told so.
	/// </summary>
	public static bool RedirectedToRunningZing()
	{
		_owner = ProfileOwner.TryClaim(ProfileKey);
		if (_owner is not null) return false;
		ProfileOwner.HandOverOrTellStuck(ProfileKey, Environment.GetCommandLineArgs().Skip(1).ToList(), OwnerPatience, StartupFailure.TellAlreadyRunning);
		return true;
	}

	/// <summary>Zing is quitting: a launch from now on starts a new Zing. On the UI thread, which claimed the profile.</summary>
	public static void Release() => (_owner ?? throw new InvalidOperationException("Release needs RedirectedToRunningZing to have claimed the profile.")).Release();

	/// <summary>Calls back on the UI thread with each later launch's command line.</summary>
	public static void Listen(Action<IReadOnlyList<string>> onSecondLaunch)
	{
		var dispatcher = DispatcherQueue.GetForCurrentThread();
		var owner = _owner ?? throw new InvalidOperationException("Listen needs RedirectedToRunningZing to have claimed the profile.");
		// On the thread pool: with the UI thread's context every read of the pipe would run there.
		TestHooks.Background(Task.Run(() => owner.ListenAsync(
			argv => Faults.Enqueue(dispatcher, () => onSecondLaunch(argv), "a second launch"),
			broken => TestHooks.Record("a second launch", broken))), "second launch listener");
	}
}
