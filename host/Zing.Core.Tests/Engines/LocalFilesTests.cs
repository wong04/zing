using Zing.Core.Engines;

namespace Zing.Core.Tests.Engines;

/// <summary>What a Zing view may fetch from the host as bytes: the background model by its engine id, and a file by a token, nothing else.</summary>
public sealed class LocalFilesTests : IAsyncLifetime
{
	private readonly RunnerFixture _engines = new(engineId: EngineIds.BackgroundModel);
	private readonly FileGrants _grants = new();
	private LocalFiles _files = null!;

	public async ValueTask InitializeAsync()
	{
		await _engines.InstallAsync();
		_files = new LocalFiles(_grants, _engines.Store);
	}

	public ValueTask DisposeAsync()
	{
		_engines.Dispose();
		return ValueTask.CompletedTask;
	}

	private static byte[] ReadAll(LocalFile file)
	{
		using (file.Content)
		{
			using var copy = new MemoryStream();
			file.Content.CopyTo(copy);
			return copy.ToArray();
		}
	}

	[Fact]
	public void The_installed_model_is_served_by_its_engine_id()
	{
		var installed = File.ReadAllBytes(_engines.Store.InstalledPath(EngineIds.BackgroundModel));

		var file = _files.Open("/zing-local/model/bgmodel");

		Assert.Equal(installed.Length, file.Length);
		Assert.Equal(installed, ReadAll(file));
	}

	[Fact]
	public void A_model_that_is_not_installed_is_an_error_not_an_empty_answer()
	{
		using var bare = new RunnerFixture(engineId: EngineIds.BackgroundModel);

		Assert.Throws<EngineNotInstalledException>(() => new LocalFiles(_grants, bare.Store).Open("/zing-local/model/bgmodel"));
	}

	[Theory]
	[InlineData("/zing-local/model/ffmpeg")]
	[InlineData("/zing-local/model/ytdlp")]
	[InlineData("/zing-local/model/BGMODEL")]
	[InlineData("/zing-local/model/bgmodel/")]
	[InlineData("/zing-local/model/bgmodel/../ffmpeg")]
	[InlineData("/zing-local/model/")]
	[InlineData("/zing-local/model")]
	[InlineData("/zing-local/")]
	[InlineData("/zing-local/engine/bgmodel")]
	[InlineData("/index.html")]
	[InlineData("")]
	[InlineData("zing-local/model/bgmodel")]
	[InlineData("/zing-local/model/bg%6Dodel")]
	public void Any_other_id_or_path_is_refused(string path)
	{
		Assert.Throws<LocalFileRefusedException>(() => _files.Open(path));
	}

	[Fact]
	public void A_token_serves_the_file_it_was_issued_for()
	{
		var path = Path.Combine(_engines.Inputs, "photo.png");
		File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
		var token = _grants.Issue(path).Token;

		var file = _files.Open($"/zing-local/file/{token}");

		Assert.Equal(5, file.Length);
		Assert.Equal([1, 2, 3, 4, 5], ReadAll(file));
	}

	[Fact]
	public void A_token_nobody_issued_is_refused()
	{
		Assert.Throws<UnknownFileGrantException>(() => _files.Open("/zing-local/file/" + new string('a', 32)));
	}

	[Theory]
	[InlineData(@"/zing-local/file/C:\Windows\win.ini")]
	[InlineData("/zing-local/file/../../Windows/win.ini")]
	[InlineData("/zing-local/file/")]
	[InlineData("/zing-local/file/abc")]
	[InlineData("/zing-local/file/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
	[InlineData("/zing-local/file/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/x")]
	public void Only_a_token_shaped_name_reaches_the_grant_list(string path)
	{
		Assert.Throws<LocalFileRefusedException>(() => _files.Open(path));
	}

	[Fact]
	public void A_granted_file_that_has_gone_is_a_plain_error()
	{
		var path = Path.Combine(_engines.Inputs, "gone.png");
		File.WriteAllBytes(path, [1]);
		var token = _grants.Issue(path).Token;
		File.Delete(path);

		Assert.Throws<FileNotFoundException>(() => _files.Open($"/zing-local/file/{token}"));
	}

	[Fact]
	public void The_model_cannot_be_removed_while_it_is_being_read_and_can_afterwards()
	{
		var file = _files.Open("/zing-local/model/bgmodel");

		Assert.Throws<EngineInUseException>(() => _engines.Store.Remove(EngineIds.BackgroundModel));
		file.Content.Dispose();
		_engines.Store.Remove(EngineIds.BackgroundModel);
	}

	[Fact]
	public void A_file_can_still_be_deleted_by_the_person_while_it_is_read()
	{
		var path = Path.Combine(_engines.Inputs, "open.png");
		File.WriteAllBytes(path, [1, 2, 3]);
		var file = _files.Open($"/zing-local/file/{_grants.Issue(path).Token}");

		File.Delete(path);

		file.Content.Dispose();
	}
}
