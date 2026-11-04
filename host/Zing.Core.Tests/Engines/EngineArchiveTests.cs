using System.Diagnostics;
using Zing.Core.Engines;

namespace Zing.Core.Tests.Engines;

/// <summary>ImageMagick's portable build is a 7z, which .NET can't open, so Windows' own tar.exe does it.</summary>
public sealed class EngineArchiveTests : IDisposable
{
	private static readonly IProgress<double> NoProgress = new InlineProgress<double>(_ => { });

	private readonly EngineFixture _fixture = new();
	private readonly string _work = Path.Combine(Path.GetTempPath(), "zing-7z-test-" + Guid.NewGuid().ToString("N"));

	public EngineArchiveTests() => Directory.CreateDirectory(Path.Combine(_work, "src", "inner"));

	public void Dispose()
	{
		_fixture.Dispose();
		Directory.Delete(_work, recursive: true);
	}

	private static string Tar => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");

	private byte[] SevenZip(string name, string[] files, bool allowParent)
	{
		var archive = Path.Combine(_work, name + ".7z");
		var start = new ProcessStartInfo(Tar) { WorkingDirectory = Path.Combine(_work, "src", "inner"), UseShellExecute = false, RedirectStandardError = true };
		if (allowParent) start.ArgumentList.Add("-P");
		foreach (var arg in new[] { "--format", "7zip", "-cf", archive }.Concat(files)) start.ArgumentList.Add(arg);
		using var process = Process.Start(start)!;
		var error = process.StandardError.ReadToEnd();
		process.WaitForExit();
		if (process.ExitCode != 0) throw new InvalidOperationException($"tar failed: {error}");
		return File.ReadAllBytes(archive);
	}

	private void Lay(string relative, string content)
	{
		var path = Path.Combine(_work, "src", relative);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
	}

	private Engine SevenZipEngine(byte[] archive, string executable) =>
		new("magick", "7.1.2", _fixture.Url("magick/magick.7z"), EngineFixture.Sha256(archive), archive.Length, executable, "magick", "ImageMagick License");

	[Fact]
	public async Task A_7z_engine_is_extracted_and_its_executable_found()
	{
		Lay("inner/magick.exe", "program");
		Lay("inner/LICENSE.txt", "licence");
		var archive = SevenZip("good", ["magick.exe", "LICENSE.txt"], allowParent: false);
		_fixture.Serve("magick/magick.7z", archive);
		var store = _fixture.Store(SevenZipEngine(archive, "magick.exe"));

		var path = await store.EnsureAsync("magick", NoProgress, CancellationToken.None);

		Assert.Equal(Path.Combine(_fixture.Root, "magick", "magick.exe"), path);
		Assert.Equal("program", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
		Assert.Equal(["magick"], _fixture.Entries());
	}

	[Fact]
	public async Task A_7z_entry_that_escapes_the_folder_is_refused_and_nothing_is_written()
	{
		Lay("inner/magick.exe", "program");
		Lay("evil.txt", "escaped");
		var archive = SevenZip("slip", ["magick.exe", "../evil.txt"], allowParent: true);
		_fixture.Serve("magick/magick.7z", archive);
		var store = _fixture.Store(SevenZipEngine(archive, "magick.exe"));

		var error = await Assert.ThrowsAsync<UnsafeArchiveException>(() => store.EnsureAsync("magick", NoProgress, CancellationToken.None));

		Assert.Contains("evil.txt", error.Message);
		Assert.Empty(_fixture.Entries());
		Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_fixture.Root)!, "evil.txt")));
	}

	[Fact]
	public async Task A_7z_without_the_executable_raises_the_named_error()
	{
		Lay("inner/LICENSE.txt", "licence");
		var archive = SevenZip("empty", ["LICENSE.txt"], allowParent: false);
		_fixture.Serve("magick/magick.7z", archive);
		var store = _fixture.Store(SevenZipEngine(archive, "magick.exe"));

		await Assert.ThrowsAsync<EngineExecutableMissingException>(() => store.EnsureAsync("magick", NoProgress, CancellationToken.None));

		Assert.Empty(_fixture.Entries());
	}
}
