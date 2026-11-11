using Microsoft.VisualBasic.FileIO;
using Zing.Core.Import;

namespace Zing.Host;

/// <summary>E2e runs never fill the real Recycle Bin: the file is deleted and the recycle goes to ZING_DIALOGS_FILE.</summary>
public sealed class FileTrash : IFileTrash
{
	public void Recycle(string path)
	{
		if (TestHooks.IsEndToEndRun)
		{
			File.Delete(path);
			TestHooks.RecordDialog("recycle", Path.GetFileName(path));
			return;
		}
		FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
	}
}
