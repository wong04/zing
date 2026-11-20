namespace Zing.Core.Extensions;

/// <summary>The picked folder isn't an unpacked extension Zing can load.</summary>
public sealed class InvalidExtensionException(string folder, string reason)
	: IOException($"{Path.GetFileName(folder.TrimEnd('\\', '/'))} isn't an extension Zing can load: {reason}");

/// <summary>No installed extension has this key; it was removed meanwhile, or the caller made it up.</summary>
public sealed class UnknownExtensionException(string key) : ArgumentException($"No installed extension is called {key}");

/// <summary>The extension has no action popup to open.</summary>
public sealed class NoExtensionPopupException(string name) : InvalidOperationException($"{name} has no popup to open");

/// <summary>Extensions aren't running this launch, so the engine can't be asked to do anything with one.</summary>
public sealed class ExtensionsNotRunningException()
	: InvalidOperationException("Extensions aren't running. Turn on Allow extensions and Stay signed in to sites, then relaunch Zing.");
