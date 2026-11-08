namespace Zing.Core.Tests;

/// <summary>
/// Classes that read or change process-wide state: <see cref="HttpClient.DefaultProxy"/> and the proxy environment variables
/// it is first built from. They run alone, after every other collection. A collection only orders its own classes, so while
/// PinnedHttpTests pointed the default at a dead proxy, another class's client on the default (the engine store's yt-dlp
/// update check) dialled that proxy and was refused; and a default first built while EngineRunnerSafetyTests had set
/// HTTPS_PROXY would send every later https request to proxy.example.
/// </summary>
[CollectionDefinition("ProcessWide", DisableParallelization = true)]
public sealed class ProcessWideCollection;
