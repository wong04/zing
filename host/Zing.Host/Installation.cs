using System.Security;
using Microsoft.Win32;
using Velopack;
using Velopack.Locators;
using GithubSource = Velopack.Sources.GithubSource;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// Zing as an installed app: Velopack's install, update and uninstall hooks, which register Zing as a browser,
/// and the update source the app's Updater uses. e2e runs never construct Velopack's UpdateManager or contact GitHub.
/// </summary>
public static class Installation
{
	private const string Repository = "https://github.com/wong04/zing";

	private static UpdateManager? _manager;

	/// <summary>
	/// The first statement of Main. Launched by the installer with a hook's arguments, it runs that hook and exits;
	/// a hook has 30 seconds, so it only writes the registry.
	/// </summary>
	public static void Run()
	{
		// Velopack always appends to %LOCALAPPDATA%\velopack\velopack.log, which an e2e run must not leave behind; the installer never starts one.
		if (TestHooks.IsEndToEndRun) return;
		VelopackApp.Build()
			.OnAfterInstallFastCallback(_ => Register())
			.OnAfterUpdateFastCallback(_ => Register())
			.OnBeforeUninstallFastCallback(_ => Unregister())
			.Run();
	}

	public static bool IsInstalled => !TestHooks.IsEndToEndRun && Manager.IsInstalled;

	/// <summary>The exe in Velopack's <c>current</c> folder, which keeps its path across updates, so the registry never goes stale.</summary>
	public static string ExePath => Path.Combine(VelopackLocator.Current.AppContentDir ?? throw new InvalidOperationException("Velopack found no install folder for Zing"), "Zing.exe");

	public static bool IsDefaultBrowser() => DefaultBrowserRegistration.IsDefault(DefaultBrowser.CurrentHttpsProgId());

	/// <summary>
	/// What the app's Updater checks: the e2e fake, Velopack for an installed build, or null for a build that isn't installed.
	/// <paramref name="quit"/> closes every window, the normal quit, once the updater is waiting for Zing to exit.
	/// </summary>
	public static IUpdateSource? UpdateSource(Action quit)
	{
		if (TestHooks.UpdateSource() is { } fake) return fake;
		if (!Manager.IsInstalled) return null;
		return new VelopackUpdateSource(Manager, quit);
	}

	private static UpdateManager Manager => _manager ??= System.Environment.GetEnvironmentVariable("ZING_UPDATE_FEED") is { Length: > 0 } feed
		? new UpdateManager(feed)
		: new UpdateManager(new GithubSource(Repository, null, false));

	private static void Register()
	{
		try
		{
			foreach (var value in DefaultBrowserRegistration.Values(ExePath))
			{
				using var key = Registry.CurrentUser.CreateSubKey(value.Key);
				key.SetValue(value.Name ?? "", value.Value);
			}
			AssociationsChanged();
		}
		catch (Exception error) when (error is UnauthorizedAccessException or IOException or SecurityException)
		{
			Faults.Warning("browser registration", error);
		}
	}

	private static void Unregister()
	{
		try
		{
			foreach (var key in DefaultBrowserRegistration.KeysToDelete()) Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
			foreach (var value in DefaultBrowserRegistration.NamedValuesToDelete())
			{
				using var key = Registry.CurrentUser.OpenSubKey(value.Key, writable: true);
				// A null name is the key's default value, and these keys are shared: never delete it.
				var name = value.Name ?? throw new InvalidOperationException($"Zing's value in {value.Key} has no name, so unregistering would delete the shared key's default value");
				key?.DeleteValue(name, throwOnMissingValue: false);
			}
			AssociationsChanged();
		}
		catch (Exception error) when (error is UnauthorizedAccessException or IOException or SecurityException)
		{
			Faults.Warning("browser unregistration", error);
		}
	}

	private const int SHCNE_ASSOCCHANGED = 0x08000000;
	private const uint SHCNF_IDLIST = 0x0;

	/// <summary>Tells Explorer and Settings to re-read file and link associations, as Microsoft's browser-registration guidance asks.</summary>
	private static void AssociationsChanged() => SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, 0, 0);

	[System.Runtime.InteropServices.DllImport("shell32.dll")]
	private static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
