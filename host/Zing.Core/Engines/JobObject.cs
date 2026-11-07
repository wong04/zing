using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Zing.Core.Engines;

/// <summary>
/// A Win32 job object that kills everything in it when its last handle closes, which Windows does for Zing's
/// own handles when Zing exits however it exits. A process assigned to it cannot break away, and neither can
/// the children it starts afterwards, so <see cref="Terminate"/> ends the whole tree. The native calls sit here
/// rather than in Zing.Host because the tree-kill test has to run them, and Zing.Core.Tests can't load Zing.Host.
/// </summary>
internal sealed class JobObject : IDisposable
{
	private const int ExtendedLimitInformation = 9;
	private const uint KillOnJobClose = 0x2000;
	private const uint BreakawayOk = 0x800;
	private const uint SilentBreakawayOk = 0x1000;

	private readonly JobHandle _handle;

	private JobObject(JobHandle handle) => _handle = handle;

	/// <exception cref="Win32Exception">Windows refused to make the job or to set its limits.</exception>
	public static JobObject Create()
	{
		var handle = CreateJobObjectW(0, null);
		if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't create the job object that ends an engine with Zing");
		var info = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KillOnJobClose } };
		if (!SetInformationJobObject(handle, ExtendedLimitInformation, ref info, Marshal.SizeOf<ExtendedLimits>()))
		{
			var error = Marshal.GetLastWin32Error();
			handle.Dispose();
			throw new Win32Exception(error, "Couldn't set the job object's limits");
		}
		return new JobObject(handle);
	}

	/// <exception cref="Win32Exception">The process is already in a job that doesn't allow nesting, or has ended.</exception>
	public void Assign(Process process)
	{
		if (!AssignProcessToJobObject(_handle, process.SafeHandle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't put the engine in its job object");
	}

	/// <summary>Ends every process in the job at once. Asking again, or after they have all ended, is harmless.</summary>
	public void Terminate()
	{
		if (!TerminateJobObject(_handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't end the engine's job object");
	}

	public bool AllowsBreakaway()
	{
		if (!QueryInformationJobObject(_handle, ExtendedLimitInformation, out var info, Marshal.SizeOf<ExtendedLimits>(), 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
		return (info.Basic.LimitFlags & (BreakawayOk | SilentBreakawayOk)) != 0;
	}

	public void Dispose() => _handle.Dispose();

	private sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
	{
		public JobHandle() : base(ownsHandle: true)
		{
		}

		protected override bool ReleaseHandle() => CloseHandle(handle);
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct BasicLimits
	{
		public long PerProcessUserTimeLimit;
		public long PerJobUserTimeLimit;
		public uint LimitFlags;
		public nuint MinimumWorkingSetSize;
		public nuint MaximumWorkingSetSize;
		public uint ActiveProcessLimit;
		public nuint Affinity;
		public uint PriorityClass;
		public uint SchedulingClass;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct IoCounters
	{
		public ulong ReadOperationCount;
		public ulong WriteOperationCount;
		public ulong OtherOperationCount;
		public ulong ReadTransferCount;
		public ulong WriteTransferCount;
		public ulong OtherTransferCount;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ExtendedLimits
	{
		public BasicLimits Basic;
		public IoCounters Io;
		public nuint ProcessMemoryLimit;
		public nuint JobMemoryLimit;
		public nuint PeakProcessMemoryUsed;
		public nuint PeakJobMemoryUsed;
	}

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern JobHandle CreateJobObjectW(nint attributes, string? name);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetInformationJobObject(JobHandle job, int infoClass, ref ExtendedLimits info, int length);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool QueryInformationJobObject(JobHandle job, int infoClass, out ExtendedLimits info, int length, nint returned);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool AssignProcessToJobObject(JobHandle job, SafeHandle process);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool TerminateJobObject(JobHandle job, uint exitCode);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(nint handle);
}
