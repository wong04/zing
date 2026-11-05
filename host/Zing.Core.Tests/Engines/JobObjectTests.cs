using System.Diagnostics;
using Zing.Core.Engines;

namespace Zing.Core.Tests.Engines;

public sealed class JobObjectTests
{
	private static Process StartSleeper()
	{
		var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Zing.FakeEngine.exe")) { UseShellExecute = false, CreateNoWindow = true };
		start.ArgumentList.Add("--sleep");
		start.ArgumentList.Add("60000");
		return Process.Start(start)!;
	}

	[Fact]
	public void Closing_the_job_kills_the_process_in_it()
	{
		using var process = StartSleeper();
		var job = JobObject.Create();
		job.Assign(process);

		job.Dispose();

		Assert.True(process.WaitForExit(5000), "the process outlived its job");
	}

	[Fact]
	public void Terminate_kills_the_process_in_it()
	{
		using var process = StartSleeper();
		using var job = JobObject.Create();
		job.Assign(process);

		job.Terminate();

		Assert.True(process.WaitForExit(5000));
	}

	[Fact]
	public void A_process_cannot_leave_the_job_by_breaking_away()
	{
		using var process = StartSleeper();
		using var job = JobObject.Create();
		job.Assign(process);

		Assert.False(job.AllowsBreakaway());
		job.Terminate();
	}
}
