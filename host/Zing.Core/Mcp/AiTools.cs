using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zing.Core.Mcp;

public sealed record ToolDef(string Name, string Description, JsonObject InputSchema);

/// <summary>
/// The MCP tools: checks the share, the URL rules and the site's confirm level, then hands the action to the page driver.
/// Every failure the AI can act on comes back as a tool result carrying an <see cref="AiErrors"/> code.
/// </summary>
public sealed class AiTools
{
	private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(250);
	private const int DefaultWaitSeconds = 10;
	private const int MaxWaitSeconds = 60;

	// Without a ref the focused element is unknown, so Enter is judged as if it were in an editable.
	private static readonly AxNode NoTarget = new("", "", true, false, null);

	private sealed class TabState
	{
		public Snapshot? Latest;
		public int Generation;
		public readonly HashSet<long> Masked = [];
	}

	private sealed record Target(long Node, AxNode Ax, string Ref);

	private readonly IPageDriver _driver;
	private readonly AiShares _shares;
	private readonly SitePolicy _policy;
	private readonly ConfirmGate _gate;
	private readonly IScheduler _scheduler;
	private readonly Dictionary<TabRef, TabState> _state = [];
	private readonly IReadOnlyList<ToolDef> _defs = Defs();

	public AiTools(IPageDriver driver, AiShares shares, SitePolicy policy, ConfirmGate gate, IScheduler scheduler)
	{
		_driver = driver;
		_shares = shares;
		_policy = policy;
		_gate = gate;
		_scheduler = scheduler;
		shares.Unshared += tab => _state.Remove(tab);
	}

	/// <summary>Set by the host from <c>aiControl.enabled</c>.</summary>
	public bool Enabled { get; set; }

	public IReadOnlyList<ToolDef> List() => _defs;

	/// <summary>Throws <see cref="UnknownToolException"/> or <see cref="ToolArgumentException"/> for a malformed call.</summary>
	public async Task<JsonNode> Call(string name, JsonObject args)
	{
		if (!_defs.Any(d => d.Name == name)) throw new UnknownToolException(name);
		if (!Enabled) return AiErrors.Result(AiErrors.Disabled, "AI control is turned off in Zing's Settings.");
		try
		{
			return await Dispatch(name, args);
		}
		catch (AiToolException error)
		{
			return AiErrors.Result(error.Code, error.Message);
		}
		catch (TabGoneException error)
		{
			return AiErrors.Result(AiErrors.TabGone, error.Message);
		}
		catch (DialogOpenException error)
		{
			return AiErrors.Result(AiErrors.DialogOpen, error.Message);
		}
		catch (NotVisibleException error)
		{
			return AiErrors.Result(AiErrors.NotVisible, error.Message);
		}
		catch (CdpTimeoutException error)
		{
			return AiErrors.Result(AiErrors.Timeout, error.Message);
		}
	}

	private Task<JsonNode> Dispatch(string name, JsonObject args) => name switch
	{
		"list_tabs" => Task.FromResult(ListTabs()),
		"snapshot" => TakeSnapshot(args),
		"read_text" => ReadText(args),
		"screenshot" => Screenshot(args),
		"open_tab" => OpenTab(args),
		"close_tab" => CloseTab(args),
		"navigate" => Navigate(args),
		"back" => Act(name, args, AiAction.Navigate, null, null, "go back", (tab, _) => _driver.Back(tab)),
		"forward" => Act(name, args, AiAction.Navigate, null, null, "go forward", (tab, _) => _driver.Forward(tab)),
		"click" => Click(args),
		"type" => TypeText(args),
		"select" => Select(args),
		"scroll" => Scroll(args),
		"press_key" => PressKey(args),
		"wait_for" => WaitFor(args),
		"download" => Download(args),
		"fill_login" => FillLogin(args),
		_ => throw new UnknownToolException(name),
	};

	private JsonNode ListTabs()
	{
		var tabs = new JsonArray();
		foreach (var (aiId, tab) in _shares.List()) tabs.Add(new JsonObject { ["tab"] = aiId, ["url"] = _driver.UrlOf(tab).ToString() });
		return Text(tabs.ToJsonString());
	}

	private async Task<JsonNode> TakeSnapshot(JsonObject args)
	{
		var tab = Resolve(args);
		var tree = await _driver.AxTree(tab);
		var state = StateOf(tab);
		state.Generation++;
		state.Latest = Snapshot.Build(tree, state.Generation, state.Masked);
		return Text(state.Latest.Text);
	}

	private async Task<JsonNode> ReadText(JsonObject args) => Text(await _driver.MainText(Resolve(args)));

	private async Task<JsonNode> Screenshot(JsonObject args)
	{
		var tab = Resolve(args);
		var node = OptionalString("screenshot", args, "ref") is { } reference ? TargetOf(tab, reference).Node : (long?)null;
		var png = await _driver.Screenshot(tab, node, new HashSet<long>(StateOf(tab).Masked));
		return new JsonObject
		{
			["content"] = new JsonArray(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" }),
		};
	}

	private async Task<JsonNode> OpenTab(JsonObject args)
	{
		var url = AllowedUrl("open_tab", args);
		// Not confirmed even on ask-all sites: there is no tab yet to show the bar on, and a new tab is reversible.
		var tab = await _driver.OpenTab(url);
		return Text(_shares.Share(tab, url));
	}

	private async Task<JsonNode> CloseTab(JsonObject args)
	{
		var tab = Resolve(args);
		var result = await Act("close_tab", args, AiAction.CloseTab, null, null, "close this tab", (t, _) => _driver.CloseTab(t));
		_shares.Unshare(tab);
		return result;
	}

	private Task<JsonNode> Navigate(JsonObject args)
	{
		var url = AllowedUrl("navigate", args);
		return Act("navigate", args, AiAction.Navigate, null, null, $"go to {url}", (tab, _) => _driver.Navigate(tab, url));
	}

	private Task<JsonNode> Click(JsonObject args)
	{
		var reference = RequiredString("click", args, "ref");
		return Act("click", args, AiAction.Click, reference, null, null, async (tab, target) =>
		{
			var hit = await _driver.NodeAtCentre(tab, target!.Node);
			if (hit != target.Node) throw new AiToolException(AiErrors.Covered, $"Something else is on top of {target.Ref}.");
			await _driver.Click(tab, target.Node);
		});
	}

	private Task<JsonNode> TypeText(JsonObject args)
	{
		var reference = RequiredString("type", args, "ref");
		var text = RequiredString("type", args, "text");
		var submit = OptionalBool("type", args, "submit") ?? false;
		var action = submit ? AiAction.TypeSubmit : AiAction.Type;
		return Act("type", args, action, reference, null, null, (tab, target) => _driver.Type(tab, target!.Node, text, submit));
	}

	private Task<JsonNode> Select(JsonObject args)
	{
		var reference = RequiredString("select", args, "ref");
		var value = RequiredString("select", args, "value");
		return Act("select", args, AiAction.Select, reference, null, null, (tab, target) => _driver.Select(tab, target!.Node, value));
	}

	private Task<JsonNode> Scroll(JsonObject args)
	{
		var reference = OptionalString("scroll", args, "ref");
		var dx = OptionalInt("scroll", args, "deltaX") ?? 0;
		var dy = OptionalInt("scroll", args, "deltaY") ?? 0;
		return Act("scroll", args, AiAction.Scroll, reference, null, "scroll", (tab, target) => _driver.Scroll(tab, target?.Node, dx, dy));
	}

	private Task<JsonNode> PressKey(JsonObject args)
	{
		var key = RequiredString("press_key", args, "key");
		var reference = OptionalString("press_key", args, "ref");
		return Act("press_key", args, AiAction.PressKey, reference, key, $"press {key}", (tab, target) => _driver.PressKey(tab, target?.Node, key));
	}

	private async Task<JsonNode> Download(JsonObject args)
	{
		var tab = Resolve(args);
		var reference = OptionalString("download", args, "ref");
		Uri url;
		if (reference is not null)
		{
			TargetOf(tab, reference);
			if (!StateOf(tab).Latest!.Links.TryGetValue(reference, out var href)) throw new ToolArgumentException("download", "ref", "is not a link");
			url = Parse(href) ?? throw new AiToolException(AiErrors.BlockedUrl, "Only http and https URLs can be downloaded.");
		}
		else url = AllowedUrl("download", args);
		if (!AiUrls.Allowed(url)) throw new AiToolException(AiErrors.BlockedUrl, "Only http and https URLs can be downloaded.");
		return await Act("download", args, AiAction.Download, reference, null, $"download {url}", (t, _) => _driver.Download(t, url));
	}

	private async Task<JsonNode> FillLogin(JsonObject args)
	{
		var tab = Resolve(args);
		var (result, filled) = await _driver.FillLogin(tab);
		EnsureShared(tab);
		if (result == FillResult.Denied) throw new AiToolException(AiErrors.Denied, "The owner didn't allow the login to be filled.");
		if (result == FillResult.NoSavedLogin) throw new AiToolException(AiErrors.NoSavedLogin, "There is no saved login for this site.");
		StateOf(tab).Masked.UnionWith(filled);
		return Text(nameof(FillResult.Filled));
	}

	private async Task<JsonNode> WaitFor(JsonObject args)
	{
		var tab = Resolve(args);
		var text = OptionalString("wait_for", args, "text");
		var reference = OptionalString("wait_for", args, "ref");
		if ((text is null) == (reference is null)) throw new ToolArgumentException("wait_for", "text", "or 'ref' must be given, not both");
		var seconds = Math.Clamp(OptionalInt("wait_for", args, "timeout") ?? DefaultWaitSeconds, 0, MaxWaitSeconds);
		var node = reference is null ? (long?)null : TargetOf(tab, reference).Node;
		var waited = TimeSpan.Zero;
		while (true)
		{
			var found = node is null ? (await _driver.MainText(tab)).Contains(text!, StringComparison.Ordinal) : HasNode(await _driver.AxTree(tab), node.Value);
			EnsureShared(tab);
			if (found) return Text("Found");
			if (waited >= TimeSpan.FromSeconds(seconds)) throw new AiToolException(AiErrors.Timeout, $"Not there after {seconds} s.");
			await Delay(PollEvery);
			waited += PollEvery;
			EnsureShared(tab);
		}
	}

	/// <summary>The common path for an action: resolve, confirm if the site asks, re-check after the confirm, then run.</summary>
	private async Task<JsonNode> Act(string tool, JsonObject args, AiAction action, string? reference, string? key, string? description, Func<TabRef, Target?, Task> run)
	{
		var tab = Resolve(args);
		var target = reference is null ? null : TargetOf(tab, reference);
		var generation = StateOf(tab).Generation;
		var url = _driver.UrlOf(tab);
		if (_policy.NeedsConfirm(url, action, target?.Ax ?? NoTarget, key))
		{
			var what = description ?? $"{tool} \"{target!.Ax.Name}\"";
			var allowed = await _gate.Ask(tab, url, what);
			EnsureShared(tab);
			if (!allowed) throw new AiToolException(AiErrors.Denied, "The owner didn't allow it.");
			if (_driver.UrlOf(tab) != url) throw new AiToolException(AiErrors.Denied, "The page changed while waiting for the owner.");
			if (target is not null && StateOf(tab).Generation != generation) throw new AiToolException(AiErrors.StaleRef, "A newer snapshot was taken; take another and use its refs.");
		}
		await run(tab, target);
		return Text("Done");
	}

	private TabRef Resolve(JsonObject args)
	{
		var aiId = args["tab"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new AiToolException(AiErrors.NotShared, "No tab was given.");
		if (_shares.TryResolve(aiId, out var tab)) return tab;
		throw new AiToolException(AiErrors.NotShared, $"Tab {aiId} isn't shared with AI.");
	}

	private void EnsureShared(TabRef tab)
	{
		if (!_shares.IsShared(tab)) throw new AiToolException(AiErrors.NotShared, "The tab was unshared.");
	}

	private TabState StateOf(TabRef tab)
	{
		if (!_state.TryGetValue(tab, out var state)) _state[tab] = state = new TabState();
		return state;
	}

	private Target TargetOf(TabRef tab, string reference)
	{
		var state = StateOf(tab);
		long node = 0;
		if (state.Latest?.TryRef(reference, state.Generation, out node) != RefLookup.Found)
			throw new AiToolException(AiErrors.StaleRef, $"{reference} isn't in the latest snapshot; take a new one.");
		return new Target(node, state.Latest.Nodes[reference], reference);
	}

	private Task Delay(TimeSpan delay)
	{
		// Not RunContinuationsAsynchronously: the poll must resume on the UI thread's synchronization context.
		var done = new TaskCompletionSource();
		_scheduler.Schedule(delay, done.SetResult);
		return done.Task;
	}

	private static bool HasNode(JsonElement tree, long backendNodeId) =>
		tree.GetProperty("nodes").EnumerateArray().Any(n => n.TryGetProperty("backendDOMNodeId", out var b) && b.GetInt64() == backendNodeId);

	private static Uri AllowedUrl(string tool, JsonObject args)
	{
		var url = Parse(RequiredString(tool, args, "url"));
		if (url is null || !AiUrls.Allowed(url)) throw new AiToolException(AiErrors.BlockedUrl, "Only http and https URLs are allowed.");
		return url;
	}

	private static Uri? Parse(string value) => Uri.TryCreate(value, UriKind.Absolute, out var url) ? url : null;

	private static string RequiredString(string tool, JsonObject args, string name) =>
		OptionalString(tool, args, name) ?? throw new ToolArgumentException(tool, name, "is required");

	private static string? OptionalString(string tool, JsonObject args, string name) => args[name] switch
	{
		null => null,
		JsonValue v when v.TryGetValue<string>(out var s) => s,
		_ => throw new ToolArgumentException(tool, name, "must be a string"),
	};

	private static bool? OptionalBool(string tool, JsonObject args, string name) => args[name] switch
	{
		null => null,
		JsonValue v when v.TryGetValue<bool>(out var b) => b,
		_ => throw new ToolArgumentException(tool, name, "must be a boolean"),
	};

	private static int? OptionalInt(string tool, JsonObject args, string name) => args[name] switch
	{
		null => null,
		JsonValue v when v.TryGetValue<int>(out var i) => i,
		JsonValue v when v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var d) && d == Math.Floor(d) && Math.Abs(d) <= int.MaxValue => (int)d,
		_ => throw new ToolArgumentException(tool, name, "must be an integer"),
	};

	private static JsonNode Text(string text) => new JsonObject
	{
		["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
	};

	private static IReadOnlyList<ToolDef> Defs()
	{
		var tab = Prop("string", "A tab id from list_tabs.");
		var reference = Prop("string", "A ref such as e12 from the latest snapshot of the tab.");
		var url = Prop("string", "An absolute http or https URL.");
		return
		[
			Def("list_tabs", "Lists the tabs the owner has shared with AI.", []),
			Def("snapshot", "An accessibility-tree outline of the tab; refs like e12 mark what can be acted on.", [], ("tab", tab)),
			Def("read_text", "The main text of the tab, including PDFs in the viewer.", [], ("tab", tab)),
			Def("screenshot", "A PNG of the visible page, or of one element.", ["ref"], ("tab", tab), ("ref", reference)),
			Def("open_tab", "Opens a new tab at a URL; it starts out shared.", [], ("url", url)),
			Def("close_tab", "Closes a shared tab.", [], ("tab", tab)),
			Def("navigate", "Goes to a URL in the tab.", [], ("tab", tab), ("url", url)),
			Def("back", "Goes back in the tab's history.", [], ("tab", tab)),
			Def("forward", "Goes forward in the tab's history.", [], ("tab", tab)),
			Def("click", "Clicks an element.", [], ("tab", tab), ("ref", reference)),
			Def("type", "Types text into an element, optionally submitting.", ["submit"], ("tab", tab), ("ref", reference),
				("text", Prop("string", "The text to type.")), ("submit", Prop("boolean", "Press Enter afterwards."))),
			Def("select", "Picks an option in a select element.", [], ("tab", tab), ("ref", reference), ("value", Prop("string", "The option's value or label."))),
			Def("scroll", "Scrolls the page or an element.", ["ref", "deltaX", "deltaY"], ("tab", tab), ("ref", reference),
				("deltaX", Prop("integer", "Pixels to the right.")), ("deltaY", Prop("integer", "Pixels down."))),
			Def("press_key", "Presses a key, such as Enter or ArrowDown, optionally on an element.", ["ref"], ("tab", tab), ("ref", reference),
				("key", Prop("string", "A KeyboardEvent.key value."))),
			Def("wait_for", "Waits until text appears on the page or a ref is in the page.", ["text", "ref", "timeout"], ("tab", tab), ("ref", reference),
				("text", Prop("string", "Text to wait for.")), ("timeout", Prop("integer", $"Seconds, at most {MaxWaitSeconds}; default {DefaultWaitSeconds}."))),
			Def("download", "Downloads a URL or a link's target into the owner's Downloads.", ["url", "ref"], ("tab", tab), ("url", url), ("ref", reference)),
			Def("fill_login", "Asks Zing to fill the tab's login form from the owner's vault. Returns only Filled.", [], ("tab", tab)),
		];
	}

	private static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

	private static ToolDef Def(string name, string description, string[] optional, params (string Name, JsonObject Schema)[] props)
	{
		var properties = new JsonObject();
		foreach (var (prop, schema) in props) properties[prop] = schema.DeepClone();
		var required = new JsonArray(props.Select(p => p.Name).Where(p => !optional.Contains(p)).Select(p => (JsonNode)p).ToArray());
		return new ToolDef(name, description, new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required });
	}
}
