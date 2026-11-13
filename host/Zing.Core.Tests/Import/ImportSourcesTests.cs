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

	private string DiaUserData(string package = "TheBrowserCompany.Dia_ttt1ap7aakyb4") => Path.Combine(root, "Packages", package, "LocalCache", "Local", "Dia", "User Data");

	[Fact]
	public void FindsDiaProfilesInsideItsPackageFolder()
	{
		var dia = DiaUserData();
		WriteLocalState(dia, "Default", ("Default", "Personal"), ("Profile 1", "Work"));
		WriteProfile(dia, "Default", Fixture("chromium-bookmarks.json"));
		WriteProfile(dia, "Profile 1", Fixture("chromium-bookmarks.json"));

		var found = ImportSources.Find(root);

		Assert.Equal(["dia:Default", "dia:Profile 1"], found.Select(s => s.Id));
		var personal = found[0];
		Assert.Equal(ImportBrowser.Dia, personal.Browser);
		Assert.Equal("Default", personal.Profile);
		Assert.Equal("Dia (Personal)", personal.Display);
		Assert.Equal(Path.Combine(dia, "Default", "Bookmarks"), personal.Path);
		Assert.Equal(LinkCount(ChromiumBookmarks.Parse(Fixture("chromium-bookmarks.json"))), personal.Bookmarks);
		Assert.True(personal.LastUsed);
	}

	[Fact]
	public void DiaSitsBetweenTheChromiumBrowsersAndArc()
	{
		var chrome = ChromeUserData();
		WriteLocalState(chrome, null, ("Default", "Personal"));
		WriteProfile(chrome, "Default", Fixture("chromium-bookmarks.json"));
		var dia = DiaUserData();
		WriteLocalState(dia, null, ("Default", "Personal"));
		WriteProfile(dia, "Default", Fixture("chromium-bookmarks.json"));
		var arc = Path.Combine(root, "Packages", "TheBrowserCompany.Arc_ttt1ap7aakyb4", "LocalCache", "Local", "Arc");
		Directory.CreateDirectory(arc);
		File.WriteAllText(Path.Combine(arc, "StorableSidebar.json"), Fixture("arc-sidebar.json"));

		Assert.Equal(["chrome:Default", "dia:Default", "arc"], ImportSources.Find(root).Select(s => s.Id));
	}

	[Fact]
	public void ADiaPackageWithoutAProfileListIsNotARow()
	{
		Directory.CreateDirectory(Path.Combine(root, "Packages", "TheBrowserCompany.Dia_ttt1ap7aakyb4", "LocalCache", "Local"));

		Assert.Empty(ImportSources.Find(root));
	}

	[Fact]
	public void ADamagedDiaProfileListIsOneDiaRowWithAnError()
	{
		var dia = DiaUserData();
		Directory.CreateDirectory(dia);
		File.WriteAllText(Path.Combine(dia, "Local State"), "{ not json");

		var row = Assert.Single(ImportSources.Find(root));

		Assert.Equal("dia", row.Id);
		Assert.Equal(ImportBrowser.Dia, row.Browser);
		Assert.Null(row.Profile);
		Assert.False(string.IsNullOrEmpty(row.Error));
		Assert.DoesNotContain(root, row.Error);
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

	[Fact]
	public void AMalformedLocalStateIsABrowserRowWithAnErrorAndOtherBrowsersAreStillFound()
	{
		var chrome = ChromeUserData();
		Directory.CreateDirectory(chrome);
		File.WriteAllText(Path.Combine(chrome, "Local State"), "{ not json");
		var edge = UserData("Microsoft", "Edge");
		WriteLocalState(edge, null, ("Default", "Me"));
		WriteProfile(edge, "Default", Fixture("chromium-bookmarks.json"));
		var arc = Path.Combine(root, "Packages", "TheBrowserCompany.Arc_x", "LocalCache", "Local", "Arc");
		Directory.CreateDirectory(arc);
		File.WriteAllText(Path.Combine(arc, "StorableSidebar.json"), Fixture("arc-sidebar.json"));

		var found = ImportSources.Find(root);

		Assert.Equal(["chrome", "edge:Default", "arc"], found.Select(s => s.Id));
		var row = found[0];
		Assert.Equal(ImportBrowser.Chrome, row.Browser);
		Assert.Null(row.Profile);
		Assert.Equal("Chrome", row.Display);
		Assert.Null(row.Bookmarks);
		Assert.False(row.LastUsed);
		Assert.Equal(Path.Combine(chrome, "Local State"), row.Path);
		Assert.False(string.IsNullOrEmpty(row.Error));
		Assert.DoesNotContain(root, row.Error);
	}

	[Fact]
	public void AFileInUseIsARowWhoseErrorHasNoPath()
	{
		var chrome = ChromeUserData();
		WriteLocalState(chrome, null, ("Default", "Personal"));
		WriteProfile(chrome, "Default", Fixture("chromium-bookmarks.json"));
		using var lockFile = new FileStream(Path.Combine(chrome, "Default", "Bookmarks"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

		var source = Assert.Single(ImportSources.Find(root));

		Assert.Null(source.Bookmarks);
		Assert.Equal("Zing couldn't read this profile's bookmarks: the file is in use or unreadable", source.Error);
	}

	[Fact]
	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	public void APackagesFolderThatCannotBeListedIsAnArcRowWithAnErrorAndChromiumIsStillFound()
	{
		var chrome = ChromeUserData();
		WriteLocalState(chrome, null, ("Default", "Personal"));
		WriteProfile(chrome, "Default", Fixture("chromium-bookmarks.json"));
		var packages = new DirectoryInfo(Path.Combine(root, "Packages"));
		packages.Create();
		var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
		var security = packages.GetAccessControl();
		var deny = new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.ListDirectory, System.Security.AccessControl.AccessControlType.Deny);
		security.AddAccessRule(deny);
		packages.SetAccessControl(security);
		try
		{
			var found = ImportSources.Find(root);

			Assert.Equal(["chrome:Default", "arc"], found.Select(s => s.Id));
			var arc = found[1];
			Assert.Equal(ImportBrowser.Arc, arc.Browser);
			Assert.Null(arc.Bookmarks);
			Assert.Equal("Zing couldn't read Arc's sidebar: access was denied", arc.Error);
		}
		finally
		{
			security.RemoveAccessRule(deny);
			packages.SetAccessControl(security);
		}
	}

	[Fact]
	public void ABookmarksFileOverTheLimitIsARowWithTheSizeError()
	{
		var chrome = ChromeUserData();
		WriteLocalState(chrome, null, ("Default", "Personal"));
		WriteProfile(chrome, "Default");
		using (var file = File.Create(Path.Combine(chrome, "Default", "Bookmarks"))) file.SetLength(ImportFile.MaxBytes + 1);

		var source = Assert.Single(ImportSources.Find(root));

		Assert.Null(source.Bookmarks);
		Assert.Equal("This file is too large to be an import. Zing reads files of up to 10 MB, which is far more than any real export.", source.Error);
	}

	[Fact]
	public void ANonUtf8BookmarksFileIsARowWithTheEncodingError()
	{
		var chrome = ChromeUserData();
		WriteLocalState(chrome, null, ("Default", "Personal"));
		WriteProfile(chrome, "Default");
		File.WriteAllBytes(Path.Combine(chrome, "Default", "Bookmarks"), [.. "{\"roots\":{\"other\":{\"name\":\"Caf"u8.ToArray(), 0xE9, .. "\"}}}"u8.ToArray()]);

		var source = Assert.Single(ImportSources.Find(root));

		Assert.Null(source.Bookmarks);
		Assert.Equal("The file isn't UTF-8 text, so it can't be an export Zing reads.", source.Error);
	}

	[Fact]
	public void ErrorsNeverCarryAPath()
	{
		var path = Path.Combine(root, "secret", "Bookmarks");
		Assert.Equal("Zing couldn't read this profile's bookmarks: the file is missing", ImportErrors.Explain(new FileNotFoundException($"Could not find {path}", path), "this profile's bookmarks"));
		Assert.Equal("Zing couldn't read this profile's bookmarks: the file is missing", ImportErrors.Explain(new DirectoryNotFoundException($"Could not find {path}"), "this profile's bookmarks"));
		Assert.Equal("Zing couldn't read this profile's bookmarks: access was denied", ImportErrors.Explain(new UnauthorizedAccessException($"Access to {path} is denied"), "this profile's bookmarks"));
		Assert.Equal("Zing couldn't read this profile's bookmarks: the file is in use or unreadable", ImportErrors.Explain(new IOException($"{path} is in use"), "this profile's bookmarks"));
		Assert.Equal(new BookmarksFileDamagedException("Bookmarks").Message, ImportErrors.Explain(new BookmarksFileDamagedException("Bookmarks"), "this profile's bookmarks"));
	}
}
