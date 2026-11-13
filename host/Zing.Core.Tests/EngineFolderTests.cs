namespace Zing.Core.Tests;

public sealed class EngineFolderTests : IDisposable
{
	private readonly string _folder = Directory.CreateTempSubdirectory("zing-engine-folder-").FullName;

	private static CancellationToken Cancel => TestContext.Current.CancellationToken;

	public void Dispose() => Directory.Delete(_folder, recursive: true);

	/// <summary>Opens the lock file as the engine does: exclusively, for as long as it runs.</summary>
	private FileStream HoldAsEngine()
	{
		var lockFile = EngineFolder.LockFile(_folder);
		Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
		return new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
	}

	[Fact]
	public async Task AFolderNoEngineEverUsedIsFree()
	{
		await EngineFolder.WaitUntilFreeAsync(_folder, TimeSpan.Zero, Cancel);
	}

	[Fact]
	public async Task AFolderALostEngineLeftItsLockFileInIsFree()
	{
		HoldAsEngine().Dispose();
		await EngineFolder.WaitUntilFreeAsync(_folder, TimeSpan.Zero, Cancel);
	}

	[Fact]
	public async Task ALaunchWaitsForAQuittingEngineToLetGo()
	{
		var engine = HoldAsEngine();
		var waiting = EngineFolder.WaitUntilFreeAsync(_folder, TimeSpan.FromSeconds(10), Cancel);
		await Task.Delay(300, Cancel);
		Assert.False(waiting.IsCompleted);
		engine.Dispose();
		await waiting.WaitAsync(TimeSpan.FromSeconds(5), Cancel);
	}

	[Fact]
	public async Task AnEngineThatNeverLetsGoFailsTheWaitWithTheReason()
	{
		using var engine = HoldAsEngine();
		var error = await Assert.ThrowsAsync<EngineFolderInUseException>(() => EngineFolder.WaitUntilFreeAsync(_folder, TimeSpan.FromMilliseconds(200), Cancel));
		Assert.Contains(_folder, error.Message);
	}
}
