using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Zing.Core;

namespace Zing.Core.Tests;

/// <summary>
/// The bridge contract (src/shared/ipc.ts InvokeContract / EventContract,
/// generated into Defaults/channels.json) checked against the handlers the
/// host actually registers, so a channel the renderer calls can't go unhandled
/// (as shell:clear-session once did) and the host can't answer one the
/// renderer no longer has.
/// </summary>
public sealed partial class ChannelContractTests : IDisposable
{
	/// <summary>
	/// Registered by Zing.Host's MainWindow, not ChromeApi, because they need the
	/// real window: the OS Default apps page, the title bar's caption colours, and
	/// the error sink that e2e runs fail on.
	/// </summary>
	private static readonly string[] HostRegistered = ["app:report-error", "chrome:titlebar-colors", "default-browser:open-settings"];

	private readonly Bridge _bridge = new();
	private readonly ChromeApi _api;

	public ChannelContractTests()
	{
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		_api = new ChromeApi(_bridge, new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), BrowsingData: new FakeBrowsingData()), new FakeWindowHost(), "test");
	}

	public void Dispose() => _api.Dispose();

	private static JsonNode Contract => ZingJson.LoadDefaults("channels.json");

	private static SortedSet<string> InvokeChannels() =>
		new(Contract["invoke"]!.AsArray().Select(e => e!["channel"]!.GetValue<string>()), StringComparer.Ordinal);

	private static SortedSet<string> EventChannels() =>
		new(Contract["events"]!.AsArray().Select(e => e!.GetValue<string>()), StringComparer.Ordinal);

	[Fact]
	public void Every_invoke_channel_has_a_handler()
	{
		var handled = _bridge.Channels.Concat(HostRegistered).ToHashSet(StringComparer.Ordinal);
		None(InvokeChannels().Where(c => !handled.Contains(c)), "invoke channels with no handler");
	}

	[Fact]
	public void Every_handler_is_in_the_contract()
	{
		var contract = InvokeChannels();
		None(_bridge.Channels.Concat(HostRegistered).Where(c => !contract.Contains(c)), "handlers for channels ipc.ts doesn't have");
	}

	[Fact]
	public void ChromeApi_and_the_host_dont_both_register_a_channel()
	{
		None(HostRegistered.Intersect(_bridge.Channels), "channels registered twice");
	}

	/// <summary>Keeps <see cref="HostRegistered"/> honest: it is exactly what Zing.Host registers.</summary>
	[Fact]
	public void The_host_registered_list_matches_the_host_source()
	{
		var registered = HostSource().SelectMany(text => RegisterCall().Matches(text).Select(m => m.Groups[1].Value)).Order(StringComparer.Ordinal);
		Assert.Equal(HostRegistered.Order(StringComparer.Ordinal), registered);
	}

	/// <summary>Every event the host pushes (window host Push, or a bridge Event sent straight to a view) is one the renderer listens for.</summary>
	[Fact]
	public void Every_pushed_event_is_in_the_contract()
	{
		var events = EventChannels();
		var pushed = CoreSource().Concat(HostSource()).SelectMany(text => PushCall().Matches(text).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
		Assert.NotEmpty(pushed);
		None(pushed.Where(c => !events.Contains(c)), "pushed events ipc.ts doesn't have");
	}

	[Fact]
	public void Invoke_arities_are_well_formed()
	{
		foreach (var entry in Contract["invoke"]!.AsArray())
		{
			if (entry!["args"] is { } args) Assert.True(args.GetValue<int>() >= 0);
			else Assert.True(entry["minArgs"]!.GetValue<int>() < entry["maxArgs"]!.GetValue<int>(), entry.ToJsonString());
		}
	}

	private static void None(IEnumerable<string> channels, string what)
	{
		var found = channels.Order(StringComparer.Ordinal).ToList();
		Assert.True(found.Count == 0, $"{what}: {string.Join(", ", found)}");
	}

	[GeneratedRegex("""Register\("([^"]+)"\s*,""")]
	private static partial Regex RegisterCall();

	[GeneratedRegex("""(?:\bPush|Bridge\.Event)\("([^"]+)"\s*,""")]
	private static partial Regex PushCall();

	private static IEnumerable<string> CoreSource() => Sources("Zing.Core");

	private static IEnumerable<string> HostSource() => Sources("Zing.Host");

	private static IEnumerable<string> Sources(string project)
	{
		var dir = Path.Combine(RepoRoot(), "host", project);
		return Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
			.Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
			.Select(File.ReadAllText);
	}

	private static string RepoRoot()
	{
		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
			if (File.Exists(Path.Combine(dir.FullName, "package.json")) && Directory.Exists(Path.Combine(dir.FullName, "host"))) return dir.FullName;
		throw new DirectoryNotFoundException("No repo root (package.json beside host/) above the test assembly");
	}
}
