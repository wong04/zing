using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace Zing.Host;

/// <summary>
/// One Zing per profile (single-instance.ts). A second launch hands its
/// command line to the running instance and exits. The key includes the
/// profile folder, so parallel e2e runs never find each other or the user's Zing.
/// </summary>
public static class SingleInstance
{
	private static string Key => "Zing-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.UserData.ToLowerInvariant())))[..16];

	/// <summary>True when another Zing owns this profile and has been handed this launch; the caller exits.</summary>
	public static bool RedirectedToRunningZing()
	{
		var owner = AppInstance.FindOrRegisterForKey(Key);
		if (owner.IsCurrent) return false;
		var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
		// The redirect must not run on this STA thread while it waits.
		Task.Run(() => owner.RedirectActivationToAsync(activation).AsTask().Wait()).Wait();
		return true;
	}

	/// <summary>Calls back on the UI thread with each later launch's command line.</summary>
	public static void Listen(Action<IReadOnlyList<string>> onSecondLaunch)
	{
		var dispatcher = DispatcherQueue.GetForCurrentThread();
		AppInstance.GetCurrent().Activated += (_, e) =>
		{
			var argv = e.Data is ILaunchActivatedEventArgs launch ? Zing.Core.LaunchArgs.Split(launch.Arguments) : [];
			dispatcher.TryEnqueue(() => onSecondLaunch(argv));
		};
	}
}
