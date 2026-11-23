using Microsoft.UI.Dispatching;
using Zing.Core;
using Zing.Core.Mcp;

namespace Zing.Host;

/// <summary>
/// Zing's MCP server, one per process: Streamable HTTP on 127.0.0.1 and the per-profile pipe for zing-mcp.exe. The
/// listeners run off the UI thread; every message is queued to it, handled there, and the reply written back from the listener.
/// </summary>
public sealed class AiEndpoint
{
	private readonly DispatcherQueue _dispatcher;
	private readonly McpHttpSessions _sessions = new();
	private readonly McpEndpoint _listeners;
	// Read on the listener's threads; replaced on the UI thread when HTTP starts or the token is regenerated.
	private volatile byte[] _current = [];

	public AiEndpoint(DispatcherQueue dispatcher, AiToken token, string pipeName, Func<string, Task<string?>> handleOnUiThread)
	{
		_dispatcher = dispatcher;
		Task<string?> Handle(string message) => OnUiThread(handleOnUiThread, message);
		_listeners = new McpEndpoint(port =>
		{
			_current = token.Current();
			return new McpHttpServer(port, () => _current, _sessions, Handle, e => TestHooks.Record("ai http", e), e => Faults.Warning("ai http client", e));
		}, () => new McpPipeServer(pipeName, Handle, e => TestHooks.Record("ai pipe", e), e => Faults.Warning("ai pipe client", e)), Faults.Warning);
		token.Regenerated += () =>
		{
			_sessions.DropAll();
			if (_listeners.HttpRunning) _current = token.Current();
			else _listeners.TokenChanged();
		};
	}

	/// <summary>Why a listener couldn't start (a port in use, a pipe name taken), for Settings; null when both run.</summary>
	public string? LastError => _listeners.LastError;

	/// <summary>On the UI thread. Never throws; starts whichever listener isn't running, so it also retries a failed one.</summary>
	public void Start(int port) => _listeners.Ensure(port);

	public void Stop()
	{
		_listeners.Stop();
		_sessions.DropAll();
	}

	private Task<string?> OnUiThread(Func<string, Task<string?>> handle, string message)
	{
		var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
		var queued = _dispatcher.TryEnqueue(() =>
		{
			Task<string?> work;
			try
			{
				work = handle(message);
			}
			catch (Exception error)
			{
				done.SetException(error);
				return;
			}
			work.ContinueWith(t =>
			{
				if (t.IsFaulted) done.SetException(t.Exception!.InnerExceptions);
				else if (t.IsCanceled) done.SetCanceled();
				else done.SetResult(t.Result);
			}, TaskScheduler.Default);
		});
		if (!queued) done.SetException(new DispatcherRefusedException("an AI request"));
		return done.Task;
	}
}

/// <summary>Until the CDP driver lands (Task 8), every page is out of reach, so the tools answer TabGone.</summary>
public sealed class NoPageDriver : IPageDriver
{
	private static T Gone<T>(TabRef tab) => throw new TabGoneException(tab);

	public Task<System.Text.Json.JsonElement> AxTree(TabRef tab) => Gone<Task<System.Text.Json.JsonElement>>(tab);
	public Task<string> MainText(TabRef tab) => Gone<Task<string>>(tab);
	public Task<byte[]> Screenshot(TabRef tab, long? node, IReadOnlySet<long> blackOut) => Gone<Task<byte[]>>(tab);
	public Task<long?> NodeAtCentre(TabRef tab, long node) => Gone<Task<long?>>(tab);
	public Task Click(TabRef tab, long node) => Gone<Task>(tab);
	public Task Type(TabRef tab, long node, string text, bool submit) => Gone<Task>(tab);
	public Task Select(TabRef tab, long node, string value) => Gone<Task>(tab);
	public Task Scroll(TabRef tab, long? node, int deltaX, int deltaY) => Gone<Task>(tab);
	public Task PressKey(TabRef tab, long? node, string key) => Gone<Task>(tab);
	public Task Navigate(TabRef tab, Uri url) => Gone<Task>(tab);
	public Task Back(TabRef tab) => Gone<Task>(tab);
	public Task Forward(TabRef tab) => Gone<Task>(tab);
	public Task<TabRef> OpenTab(Uri url) => Gone<Task<TabRef>>(new TabRef("", ""));
	public Task CloseTab(TabRef tab) => Gone<Task>(tab);
	public Task Download(TabRef tab, Uri url) => Gone<Task>(tab);
	public Task<(FillResult Result, IReadOnlySet<long> FilledNodes)> FillLogin(TabRef tab) => Gone<Task<(FillResult, IReadOnlySet<long>)>>(tab);
	public Uri UrlOf(TabRef tab) => Gone<Uri>(tab);
}
