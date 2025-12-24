// ModernTaskManager.Core/Native/NtDll.cs

using System;
using System.Runtime.InteropServices;

namespace ModernTaskManager.Core.Native
{
    public static class NtDll
    {
        public const int SystemProcessInformation = 5;
        public const int SystemPerformanceInformation = 2;
        public const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

        [DllImport("ntdll.dll", SetLastError = true)]
        public static extern uint NtQuerySystemInformation(
            int SystemInformationClass,
            IntPtr SystemInformation,
            uint SystemInformationLength,
            out uint ReturnLength);

        [StructLayout(LayoutKind.Sequential)]
        public struct UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_PROCESS_INFORMATION
        {
            public uint NextEntryOffset;
            public uint NumberOfThreads;
            public long SpareLi1;
            public long SpareLi2;
            public long SpareLi3;
            public long CreateTime;
            public long UserTime;
            public long KernelTime;
            public UNICODE_STRING ImageName;
            public int BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
            public uint HandleCount;
            public uint SessionId;
            public IntPtr PageDirectoryBase;
            public IntPtr PeakVirtualSize;
            public IntPtr VirtualSize;
            public uint PageFaultCount;
            public IntPtr PeakWorkingSetSize;
            public IntPtr WorkingSetSize;
            public IntPtr QuotaPeakPagedPoolUsage;
            public IntPtr QuotaPagedPoolUsage;
            public IntPtr QuotaPeakNonPagedPoolUsage;
            public IntPtr QuotaNonPagedPoolUsage;
            public IntPtr PagefileUsage;
            public IntPtr PeakPagefileUsage;
            public IntPtr PrivatePageCount;
            public long ReadOperationCount;
            public long WriteOperationCount;
            public long OtherOperationCount;
            public long ReadTransferCount;
            public long WriteTransferCount;
            public long OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_PERFORMANCE_INFORMATION
        {
            public long IdleProcessTime;
            public long IoReadTransferCount;
            public long IoWriteTransferCount;
            public long IoOtherTransferCount;
            public uint IoReadOperationCount;
            public uint IoWriteOperationCount;
            public uint IoOtherOperationCount;
            public uint AvailablePages;
            public uint CommittedPages;
            public uint CommitLimit;
            public uint PeakCommitment;
            public uint PageFaultCount;
            public uint CopyOnWriteCount;
            public uint TransitionCount;
            public uint CacheTransitionCount;
            public uint DemandZeroCount;
            public uint PageReadCount;
            public uint PageReadIoCount;
            public uint CacheReadCount;
            public uint CacheIoCount;
            public uint DirtyPagesWriteCount;
            public uint DirtyWriteIoCount;
            public uint MappedPagesWriteCount;
            public uint MappedWriteIoCount;
            public uint PagedPoolPages;
            public uint NonPagedPoolPages;
            public uint PagedPoolAllocs;
            public uint PagedPoolFrees;
            public uint NonPagedPoolAllocs;
            public uint NonPagedPoolFrees;
            public uint FreeSystemPtes;
            public uint SystemCodePage;
            public uint TotalSystemDriverPages;
            public uint TotalSystemCodePages;
            public uint NonPagedPoolLookasideHits;
            public uint PagedPoolLookasideHits;
            public uint AvailablePagedPoolPages;
            public uint ResidentSystemCachePage;
            public uint ResidentPagedPoolPage;
            public uint ResidentSystemDriverPage;
            public uint CcFastReadNoWait;
            public uint CcFastReadWait;
            public uint CcFastReadResourceMiss;
            public uint CcFastReadNotPossible;
            public uint CcFastMdlReadNoWait;
            public uint CcFastMdlReadWait;
            public uint CcFastMdlReadResourceMiss;
            public uint CcFastMdlReadNotPossible;
            public uint CcMapDataNoWait;
            public uint CcMapDataWait;
            public uint CcMapDataNoWaitMiss;
            public uint CcMapDataWaitMiss;
            public uint CcPinReadNoWait;
            public uint CcPinReadWait;
            public uint CcPinReadNoWaitMiss;
            public uint CcPinReadWaitMiss;
            public uint CcCopyReadNoWait;
            public uint CcCopyReadWait;
            public uint CcCopyReadNoWaitMiss;
            public uint CcCopyReadWaitMiss;
            public uint CcMdlReadNoWait;
            public uint CcMdlReadWait;
            public uint CcMdlReadNoWaitMiss;
            public uint CcMdlReadWaitMiss;
            public uint CcReadAheadIos;
            public uint CcLazyWriteIos;
            public uint CcLazyWritePages;
            public uint CcDataFlushes;
            public uint CcDataPages;
            public uint ContextSwitches;
            public uint FirstLevelTbFills;
            public uint SecondLevelTbFills;
            public uint SystemCalls;
            public uint CcTotalDirtyPages;
            public uint CcDirtyPageThreshold;
            public long ResidentAvailablePages;
            public long KernelTime;
            public long UserTime;
        }
    }
}