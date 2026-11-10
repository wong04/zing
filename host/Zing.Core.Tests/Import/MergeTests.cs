using System.Text.Json;
using System.Text.Json.Nodes;
using Zing.Core.Import;

namespace Zing.Core.Tests.Import;

/// <summary>The C# twin of mergeImport in src/shared/import/importResult.ts, held to it by merge.vectors.json.</summary>
public sealed class MergeTests
{
	private const string VectorsPath = "src/shared/import/merge.vectors.json";

	public static TheoryData<string> Names() => [.. Vectors.Load(VectorsPath).GetProperty("merge").EnumerateArray().Select(v => v.GetProperty("name").GetString()!)];

	private static JsonElement Case(string name) => Vectors.Load(VectorsPath).GetProperty("merge").EnumerateArray().Single(v => v.GetProperty("name").GetString() == name);

	private static Func<string> Counter()
	{
		var next = 0;
		return () => $"n{++next}";
	}

	[Theory]
	[MemberData(nameof(Names))]
	public void Merge_matches_the_shared_vectors(string name)
	{
		var c = Case(name);
		var library = c.GetProperty("library").Deserialize<BrowserLibrary>(ZingJson.Options)!;
		var result = c.GetProperty("result").Deserialize<BookmarkImport>(ZingJson.Options)!;
		var expected = c.GetProperty("expected");

		var outcome = ImportMerge.Merge(library, result, c.GetProperty("source").GetString()!, c.GetProperty("now").GetInt64(), Counter());

		LibraryTests.AssertSame(expected.GetProperty("library"), outcome.Library, name);
		Assert.Equal(expected.GetProperty("addedToBar").GetInt32(), outcome.AddedToBar);
		Assert.Equal(expected.GetProperty("addedToFolder").GetInt32(), outcome.AddedToFolder);
		Assert.Equal(expected.GetProperty("folderTitle").GetString(), outcome.FolderTitle);
		if (JsonNode.DeepEquals(JsonNode.Parse(c.GetProperty("library").GetRawText()), JsonNode.Parse(expected.GetProperty("library").GetRawText())))
			Assert.Same(library, outcome.Library);
	}

	[Fact]
	public void Unchanged_folders_are_returned_as_the_same_object()
	{
		var dev = new BookmarkFolder("d", "Dev", [new BookmarkLink("l", "MDN", "https://mdn.example/", 1)], 1, "Chrome");
		var library = Library.WithFavourites(Library.EmptyLibrary(1), [dev], 1);
		var result = new BookmarkImport([new ImportedFolder("Dev", [new ImportedLink("MDN", "https://mdn.example/")])], [], []);

		var outcome = ImportMerge.Merge(library, result, "Chrome", 7, Counter());

		Assert.Same(dev, Library.Favourites(outcome.Library).Single());
		Assert.Equal(0, outcome.AddedToBar);
	}

	[Fact]
	public void ImportSavesAndNotifies()
	{
		var saved = new List<BrowserLibrary>();
		var heard = new List<BrowserLibrary>();
		var store = new LibraryStore(Library.EmptyLibrary(1), saved.Add, () => 7, Counter());
		store.Subscribe(heard.Add);
		var result = new BookmarkImport([new ImportedLink("GitHub", "https://github.com/")], [new ImportedLink("News", "https://news.example/")], []);

		var outcome = store.Import(result, "Chrome");

		Assert.Equal(1, outcome.AddedToBar);
		Assert.Equal(1, outcome.AddedToFolder);
		Assert.Single(saved);
		Assert.Single(heard);
		Assert.Same(outcome.Library, saved[0]);
		Assert.Same(outcome.Library, heard[0]);
		Assert.Same(outcome.Library, store.Snapshot);
	}
}
