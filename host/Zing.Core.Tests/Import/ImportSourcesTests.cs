using System.Text.Json;
using Zing.Core.Import;

namespace Zing.Core.Tests.Import;

public sealed class ImportSourcesTests : IDisposable
{
	private const string VectorsPath = "src/shared/import/sources.vectors.json";

	private readonly string root = Path.Combine(Path.GetTempPath(), "zing-sources-" + Guid.NewGuid().ToString("N"));

	public ImportSourcesTests() => Directory.CreateDirectory(root);

	public void Dispose() => Directory.Delete(root, recursive: true);

	public static TheoryData<string> Names() => [.. Vectors.Load(VectorsPath).GetProperty("profileNames").EnumerateArray().Select(v => v.GetProperty("name").GetString()!)];

	private static string Fixture(string name) => File.ReadAllText(Path.Combine(Vectors.RepoRoot(), "src/shared/import/fixtures", name));

	private static int LinkCount(BookmarkImport import) => BookmarkImport.CountLinks(import.Bar) + BookmarkImport.CountLinks(import.Other);

	private string UserData(params string[] relative) => Path.Combine([root, .. relative, "User Data"]);

	private string ChromeUserData() => UserData("Google", "Chrome");

	private static void WriteLocalState(string userData, string? lastUsed, params (string Dir, string Name)[] profiles)
	{
		Directory.CreateDirectory(userData);
		var cache = profiles.ToDictionary(p => p.Dir, p => new { name = p.Name });
		File.WriteAllText(Path.Combine(userData, "Local State"), JsonSerializer.Serialize(new { profile = new { info_cache = cache, last_used = lastUsed } }));
	}

	private static void WriteProfile(string userData, string dir, string? bookmarks = null)
	{
		Directory.CreateDirectory(Path.Combine(userData, dir));
		if (bookmarks is not null) File.WriteAllText(Path.Combine(userData, dir, "Bookmarks"), bookmarks);
	}

	[Theory]
	[MemberData(nameof(Names))]
	public void ProfileNames_match_the_shared_vectors(string name)
	{
		var c = Vectors.Load(VectorsPath).GetProperty("profileNames").EnumerateArray().Single(v => v.GetProperty("name").GetString() == name);
		var expected = c.GetProperty("expected").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);

		Assert.Equal(expected, ImportSources.ProfileNames(c.GetProperty("localState").GetRawText()));
	}

	[Fact]
	public void FindsEachChromiumProfileWithItsDisplayName()
	{
		var chrome = ChromeUserData();
		var edge = UserData("Microsoft", "Edge");
		var brave = UserData("BraveSoftware", "Brave-Browser");
		WriteLocalState(chrome, null, ("Default", "Personal"), ("Profile 1", "Work"));
		WriteProfile(chrome, "Default", Fixture("chromium-bookmarks.json"));
		WriteProfile(chrome, "Profile 1", Fixture("chromium-bookmarks.json"));
		WriteLocalState(edge, null, ("Default", "Me"));
		WriteProfile(edge, "Default", Fixture("chromium-bookmarks.json"));
		WriteLocalState(brave, null, ("Default", "Shields"));
		WriteProfile(brave, "Default", Fixture("chromium-bookmarks.json"));

		var found = ImportSources.Find(root);

		Assert.Equal(["Chrome (Personal)", "Chrome (Work)", "Edge (Me)", "Brave (Shields)"], found.Select(s => s.Display));
		Assert.Equal(["chrome:Default", "chrome:Profile 1", "edge:Default", "brave:Default"], found.Select(s => s.Id));
		var work = found.Single(s => s.Id == "chrome:Profile 1");
		Assert.Equal(ImportBrowser.Chrome, work.Browser);
		Assert.Equal("Profile 1", work.Profile);
		Assert.Equal(Path.Combine(chrome, "Profile 1", "Bookmarks"), work.Path);
		Assert.Equal(LinkCount(ChromiumBookmarks.Parse(Fixture("chromium-bookmarks.json"))), work.Bookmarks);
		Assert.Null(work.Error);
	}

	[Fact]
	public void ListedProfilesWithoutABookmarksFileGetNoRow()
	{
		var chrome = ChromeUserData();
		WriteLocalState(chrome, null, ("Default", "Personal"), ("Profile 1", "Fresh"));
		WriteProfile(chrome, "Default", Fixture("chromium-bookmarks.json"));
		WriteProfile(chrome, "Profile 1");

		Assert.Equal(["chrome:Default"], ImportSources.Find(root).Select(s => s.Id));
	}

	[Fact]
	public void SkipsGuestAndSystemProfiles()
	{
		var chrome = ChromeUserData();
		WriteLocalState(chrome, null, ("Default", "Personal"), ("Guest Profile", "Guest"), ("System Profile", "System"));
		foreach (var dir in new[] { "Default", "Guest Profile", "System Profile" }) WriteProfile(chrome, dir, Fixture("chromium-bookmarks.json"));

		Assert.Equal(["chrome:Default"], ImportSources.Find(root).Select(s => s.Id));
	}

	[Fact]
	public void FindsArcUnderItsPackageFolder()
	{
		var arc = Path.Combine(root, "Packages", "TheBrowserCompany.Arc_ttt1ap7aakyb4", "LocalCache", "Local", "Arc");
		Directory.CreateDirectory(arc);
		File.WriteAllText(Path.Combine(arc, "StorableSidebar.json"), Fixture("arc-sidebar.json"));

		var source = Assert.Single(ImportSources.Find(root));

		Assert.Equal("arc", source.Id);
		Assert.Equal(ImportBrowser.Arc, source.Browser);
		Assert.Null(source.Profile);
		Assert.Equal("Arc", source.Display);
		Assert.Equal(Path.Combine(arc, "StorableSidebar.json"), source.Path);
		Assert.Equal(LinkCount(ArcSidebar.Parse(Fixture("arc-sidebar.json"))), source.Bookmarks);
	}

	[Fact]
	public void ACorruptBookmarksFileIsARowWithAnError()
	{
		var chrome = ChromeUserData();
		WriteLocalState(chrome, null, ("Default", "Personal"), ("Profile 1", "Work"));
		WriteProfile(chrome, "Default", Fixture("invalid.json"));
		WriteProfile(chrome, "Profile 1", Fixture("not-chromium.json"));

		var found = ImportSources.Find(root);

		var damaged = found.Single(s => s.Id == "chrome:Default");
		Assert.Null(damaged.Bookmarks);
		Assert.Equal(new BookmarksFileDamagedException("Bookmarks").Message, damaged.Error);
		var wrong = found.Single(s => s.Id == "chrome:Profile 1");
		Assert.Null(wrong.Bookmarks);
		Assert.False(string.IsNullOrEmpty(wrong.Error));
		Assert.Throws<BookmarksFileDamagedException>(() => ImportSources.Read(damaged));
	}

	[Fact]
	public void NoBrowsersFindsNothing() => Assert.Empty(ImportSources.Find(root));

	[Fact]
	public void MarksTheLastUsedProfile()
	{
		var chrome = ChromeUserData();
		WriteLocalState(chrome, "Profile 1", ("Default", "Personal"), ("Profile 1", "Work"));
		WriteProfile(chrome, "Default", Fixture("chromium-bookmarks.json"));
		WriteProfile(chrome, "Profile 1", Fixture("chromium-bookmarks.json"));

		Assert.Equal(["chrome:Profile 1"], ImportSources.Find(root).Where(s => s.LastUsed).Select(s => s.Id));
	}

	[Fact]
	public void ReadsAFileAnotherProcessHoldsOpenForWriting()
	{
		var chrome = ChromeUserData();
		WriteLocalState(chrome, null, ("Default", "Personal"));
		WriteProfile(chrome, "Default", Fixture("chromium-bookmarks.json"));
		using var writer = new FileStream(Path.Combine(chrome, "Default", "Bookmarks"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

		var source = Assert.Single(ImportSources.Find(root));

		Assert.Null(source.Error);
		Assert.True(source.Bookmarks > 0);
		Assert.NotEmpty(ImportSources.Read(source).Bar);
	}
}
