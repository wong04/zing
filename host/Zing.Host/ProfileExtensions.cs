using Microsoft.Web.WebView2.Core;
using Zing.Core.Extensions;

namespace Zing.Host;

/// <summary>The browsing profile's extensions through WebView2 (Settings' Extensions, ExtensionService).</summary>
public sealed class ProfileExtensions(CoreWebView2Profile profile) : IExtensionEngine
{
	public async Task<IReadOnlyList<EngineExtension>> ListAsync() => [.. (await profile.GetBrowserExtensionsAsync()).Select(Of)];

	public async Task<EngineExtension> AddAsync(string folder) => Of(await profile.AddBrowserExtensionAsync(folder));

	public async Task SetEnabledAsync(string id, bool enabled) => await (await FindAsync(id)).EnableAsync(enabled);

	public async Task RemoveAsync(string id) => await (await FindAsync(id)).RemoveAsync();

	private async Task<CoreWebView2BrowserExtension> FindAsync(string id) =>
		(await profile.GetBrowserExtensionsAsync()).FirstOrDefault(e => e.Id == id) ?? throw new UnknownExtensionException(id);

	private static EngineExtension Of(CoreWebView2BrowserExtension extension) => new(extension.Id, extension.Name, extension.IsEnabled);
}
