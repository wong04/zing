using Microsoft.UI.Xaml;
using WinRT;

namespace Zing.Host;

public static class Program
{
	[STAThread]
	public static void Main()
	{
		TestHooks.RecordUnhandledErrors();
		ComWrappersSupport.InitializeComWrappers();
		Application.Start(_ => new ZingApp());
	}
}

public sealed class ZingApp : Application
{
	private MainWindow? _window;

	public ZingApp()
	{
		// WinUI's XamlControlsResources isn't loaded: the project has no XAML
		// resources (it throws), and Zing's UI is the React renderer anyway.
		UnhandledException += (_, e) => TestHooks.Record("xaml", e.Exception);
	}

	protected override void OnLaunched(LaunchActivatedEventArgs args)
	{
		_window = new MainWindow();
		_window.Activate();
	}
}
