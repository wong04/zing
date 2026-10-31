using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zing.Core;

/// <summary>The page a tab shows. The WinUI host implements it over a WebView2; tests use a fake.</summary>
public interface IPage
{
	void Navigate(string url);
	bool CanGoBack { get; }
	bool CanGoForward { get; }
	void GoBack();
	void GoForward();
	void Reload();
	bool IsLoading { get; }
	void Stop();
}

/// <summary>
/// Answers the chrome renderer's bridge calls (W1: one window, one tab).
/// Mirrors src/main/ipc-handlers.ts and command-handlers.ts for what the
/// chrome needs to start and browse; every other channel stays an explicit
/// NotImplementedError until its W2/W3 step ports it.
/// </summary>
public sealed class ChromeApi
{
	private readonly TabStore _tabs;
	private readonly Address _address;
	private readonly JsonObject _settings;
	private readonly IReadOnlyDictionary<string, string> _commandLabels;
	private readonly Func<string, IPage> _pageFor;
	private readonly Action<string, object?> _push;

	public ChromeApi(Bridge bridge, TabStore tabs, Address address, JsonObject settings, JsonNode library, Func<string, IPage> pageFor, Action<string, object?> push, string appVersion)
	{
		_tabs = tabs;
		_address = address;
		_settings = settings;
		_pageFor = pageFor;
		_push = push;
		_commandLabels = ZingJson.LoadDefaults("commands.json").AsArray()
			.ToDictionary(c => c!["id"]!.GetValue<string>(), c => c!["label"]!.GetValue<string>(), StringComparer.Ordinal);

		bridge.Register("tabs:get", _ => Snapshot());
		bridge.Register("tabs:activate", args => Activate(Text(args, 0)));
		bridge.Register("tabs:navigate", args => Navigate(Text(args, 0), Text(args, 1)));
		bridge.Register("command:run", args => RunCommand(Text(args, 0)));
		bridge.Register("settings:get", _ => _settings);
		bridge.Register("library:get", _ => library);
		// W1 has no account gate yet (W3 ports src/main/account.ts); the window opens signed in.
		bridge.Register("account:get", _ => new { hasAccount = false, signedIn = true, needsPasswordReset = false, requirePasswordOnLaunch = false });
		bridge.Register("shell:get", _ => new { settingsOpen = false, settingsCategory = "general", sidebarRevealed = false, gated = false, origin = new { x = 0, y = 0 }, appVersion });
		bridge.Register("toolpanel:get", _ => new { panels = Array.Empty<object>(), dragging = false });
	}

	public TabsState Snapshot() => _tabs.Snapshot(Setting("tabOrganization"), _settings["tabDropdowns"]!.GetValue<bool>(), PageStateOf);

	private PageState PageStateOf(string id)
	{
		var page = _pageFor(id);
		return new PageState(page.IsLoading, page.CanGoBack, page.CanGoForward);
	}

	/// <summary>Called whenever a tab's page reports a change; the chrome redraws from the pushed state.</summary>
	public void TabsChanged() => _push("tabs:state", Snapshot());

	private object? Activate(string id)
	{
		_tabs.Activate(id);
		TabsChanged();
		return null;
	}

	private object? Navigate(string id, string input)
	{
		_tabs.Get(id);
		if (string.IsNullOrWhiteSpace(input)) return null;
		_pageFor(id).Navigate(_address.Resolve(input, Setting("searchEngine")));
		return null;
	}

	private object RunCommand(string command)
	{
		var page = _tabs.ActiveId is { } active ? _pageFor(active) : null;
		return command switch
		{
			"history.back" => Act(page is { CanGoBack: true }, () => page!.GoBack()),
			"history.forward" => Act(page is { CanGoForward: true }, () => page!.GoForward()),
			"page.reload" => Act(page is not null, () => page!.Reload()),
			"page.stop" => Act(page is { IsLoading: true }, () => page!.Stop()),
			"location.open" => Act(true, () => _push("chrome:focus-address", null)),
			_ => new { status = "not-implemented", label = Label(command) },
		};
	}

	private static object Act(bool applicable, Action action)
	{
		if (!applicable) return new { status = "not-applicable" };
		action();
		return new { status = "done" };
	}

	private string Label(string command) =>
		_commandLabels.TryGetValue(command, out var label) ? label : throw new UnknownCommandException(command);

	private string Setting(string key) => _settings[key]?.GetValue<string>() ?? throw new InvalidDataException($"Setting {key} is missing");

	private static string Text(JsonElement[] args, int index)
	{
		if (index >= args.Length || args[index].ValueKind != JsonValueKind.String) throw new ArgumentException($"Argument {index} must be a string");
		return args[index].GetString()!;
	}
}

public sealed class UnknownCommandException(string command) : Exception($"Unknown command {command}");
