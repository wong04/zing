using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// Follows the fullscreen of a page's PDF viewer (ViewerFullScreen) over the page's DevTools session: it finds the viewer's
/// guest targets, attaches to each, installs the reporting script and hears its reports. The page's own
/// ContainsFullScreenElement never covers the guest, so without this the viewer's fullscreen button left Zing's window as it was.
/// </summary>
public sealed class ViewerGuests(CoreWebView2 core, Action changed, Action<Task, string> background)
{
	private readonly ViewerFullScreen _state = new();

	public bool IsFullScreen => _state.IsFullScreen;

	public void Start()
	{
		core.GetDevToolsProtocolEventReceiver("Runtime.bindingCalled").DevToolsProtocolEventReceived += (_, e) => TestHooks.Guard(() => Reported(e.SessionId, e.ParameterObjectAsJson), "viewer fullscreen");
	}

	/// <summary>
	/// A document finished loading: its viewers are attached to. The page's session hears no Target.targetCreated through
	/// WebView2, so they are looked for here rather than announced.
	/// </summary>
	public void Loaded() => background(FindAsync(), "viewer guests");

	/// <summary>A new document: the last one's viewers went with it.</summary>
	public void Left()
	{
		if (_state.Left()) changed();
	}

	/// <summary>Takes every fullscreen viewer of the page out of fullscreen, as Esc there does.</summary>
	public void Exit()
	{
		foreach (var session in _state.FullScreenSessions)
			background(core.CallDevToolsProtocolMethodForSessionAsync(session, "Runtime.evaluate", JsonSerializer.Serialize(new { expression = ViewerFullScreen.ExitScript })).AsTask(), "exit viewer fullscreen");
	}

	private async Task FindAsync()
	{
		if (!_state.KnowsPage)
		{
			using var info = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Target.getTargetInfo", "{}"));
			_state.PageIs(info.RootElement.GetProperty("targetInfo").GetProperty("targetId").GetString() ?? throw new InvalidOperationException("Target.getTargetInfo gave no target id"));
		}
		using var targets = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Target.getTargets", "{}"));
		foreach (var target in targets.RootElement.GetProperty("targetInfos").EnumerateArray())
		{
			var id = target.GetProperty("targetId").GetString()!;
			var parent = target.TryGetProperty("parentId", out var p) ? p.GetString() : null;
			if (_state.IsNewGuest(id, target.GetProperty("type").GetString() ?? "", parent)) await AttachAsync(id);
		}
	}

	private async Task AttachAsync(string targetId)
	{
		using var attached = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Target.attachToTarget", JsonSerializer.Serialize(new { targetId, flatten = true })));
		var session = attached.RootElement.GetProperty("sessionId").GetString()!;
		_state.Attached(targetId, session);
		await core.CallDevToolsProtocolMethodForSessionAsync(session, "Runtime.addBinding", JsonSerializer.Serialize(new { name = ViewerFullScreen.Binding }));
		// The guest's document may not be the viewer's yet: the script goes on every new document and on the current one.
		// Without Page.enable the guest's next document never ran it, and a viewer that had only just loaded went unheard.
		await core.CallDevToolsProtocolMethodForSessionAsync(session, "Page.enable", "{}");
		await core.CallDevToolsProtocolMethodForSessionAsync(session, "Page.addScriptToEvaluateOnNewDocument", JsonSerializer.Serialize(new { source = ViewerFullScreen.Script }));
		await core.CallDevToolsProtocolMethodForSessionAsync(session, "Runtime.evaluate", JsonSerializer.Serialize(new { expression = ViewerFullScreen.Script }));
	}

	private void Reported(string session, string json)
	{
		using var called = JsonDocument.Parse(json);
		var root = called.RootElement;
		if (_state.Reported(session, root.GetProperty("name").GetString()!, root.GetProperty("payload").GetString()!)) changed();
	}
}
