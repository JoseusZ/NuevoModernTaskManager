// ModernTaskManager.Core/Native/D3DKMT.cs
// Full, defensive D3DKMT bindings for querying video memory statistics.
// Compatible approach for Windows 7..11. Avoids unsafe code.
//
// Notes:
//  - Uses D3DKMTQueryStatistics with type D3DKMT_QUERYSTATISTICS_VIDEO_MEMORY.
//  - Allocates a reasonably large buffer and scans multiple candidate offsets to find plausible values.
//  - Tries to read adapter-level fields and also attempts to find segment-group-like layouts used on newer drivers.
//  - Returns null if no plausible data found.
//  - No unsafe code, all Marshal calls and defensive checks.
//
// Author: JoseusZ
using System;
using System.Runtime.InteropServices;

namespace ModernTaskManager.Core.Native
{
    public static class D3DKMT
    {
        public const uint STATUS_SUCCESS = 0;

        public const uint D3DKMT_QUERYSTATISTICS_ADAPTER = 0;
        public const uint D3DKMT_QUERYSTATISTICS_PROCESS = 1;
        public const uint D3DKMT_QUERYSTATISTICS_PROCESS_ADAPTER = 2;
        public const uint D3DKMT_QUERYSTATISTICS_SEGMENT = 3;
        public const uint D3DKMT_QUERYSTATISTICS_VIDEO_MEMORY = 4;

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct D3DKMT_OPENADAPTERFROMGDIDISPLAYNAME
        {
            // Buffer size of 32 characters (matching earlier code)
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            public uint hAdapter;
            public LUID AdapterLuid;
            public uint VidPnSourceId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3DKMT_ADAPTERINFO
        {
            public uint hAdapter;
            public LUID AdapterLuid;
            public ulong NumOfSegments;
            public ulong NodeCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3DKMT_QUERYSTATISTICS_RESULT_ADAPTER
        {
            public uint NodeCount;
            public uint SegmentCount;
            public ulong DeviceSharedSystemMemory;
            public ulong DeviceDedicatedSystemMemory;
        }

        // Represent a possible per-segment video memory entry (simplified)
        [StructLayout(LayoutKind.Sequential)]
        public struct D3DKMT_QUERYSTATISTICS_VIDEO_MEMORY_SEGMENT
        {
            public uint SegmentId;
            public ulong BudgetBytes;
            public ulong CurrentUsageBytes;
            public ulong CommittedBytes;
            public ulong ExternalUsageBytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3DKMT_VIDEO_MEMORY_SUMMARY
        {
            public ulong TotalDedicatedBytes;
            public ulong TotalSharedBytes;
            public ulong TotalBudgetBytes;
            public ulong TotalCurrentUsage;
            public ulong TotalCommittedBytes;
        }

        // Main query structure - make it large so we can read various offsets safely
        [StructLayout(LayoutKind.Explicit, Size = 8192)]
        public struct D3DKMT_QUERYSTATISTICS
        {
            [FieldOffset(0)]
            public uint Type;
            [FieldOffset(4)]
            public LUID AdapterLuid;
            [FieldOffset(12)]
            public uint hProcess;
            [FieldOffset(16)]
            public D3DKMT_QUERYSTATISTICS_RESULT_ADAPTER ResultAdapter;
            // other fields/union - we don't declare everything because layout varies across drivers
        }

        // P/Invoke signatures
        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern uint D3DKMTOpenAdapterFromGdiDisplayName(ref D3DKMT_OPENADAPTERFROMGDIDISPLAYNAME pData);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern uint D3DKMTCloseAdapter(ref uint hAdapter);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern uint D3DKMTQueryStatistics(IntPtr pData);

        // High-level result container
        public class VideoMemoryStats
        {
            public ulong TotalDedicatedBytes { get; internal set; }
            public ulong TotalSharedBytes { get; internal set; }
            public ulong TotalBudgetBytes { get; internal set; }
            public ulong TotalCurrentUsageBytes { get; internal set; }
            public ulong TotalCommittedBytes { get; internal set; }
            public bool IsValid { get; internal set; } = false;

            public override string ToString()
            {
                return $"Dedicated={TotalDedicatedBytes}, Shared={TotalSharedBytes}, Budget={TotalBudgetBytes}, Usage={TotalCurrentUsageBytes}, Committed={TotalCommittedBytes}, Valid={IsValid}";
            }
        }

        /// <summary>
        /// Query video memory statistics using D3DKMTQueryStatistics.
        /// Uses a defensive scanning approach to cope with different driver/OS layouts.
        /// Returns null when not available or not plausible.
        /// </summary>
        public static VideoMemoryStats? QueryVideoMemoryStatistics()
        {
            // Size: large buffer to allow scanning of many offsets safely
            int bufferSize = 8192;
            IntPtr pBuffer = IntPtr.Zero;

            try
            {
                pBuffer = Marshal.AllocHGlobal(bufferSize);

                // zero buffer
                for (int i = 0; i < bufferSize; i++)
                    Marshal.WriteByte(pBuffer, i, 0);

                // Set query type to VIDEO_MEMORY
                Marshal.WriteInt32(pBuffer, 0, (int)D3DKMT_QUERYSTATISTICS_VIDEO_MEMORY);

                // Call native
                uint status = D3DKMTQueryStatistics(pBuffer);
                if (status != STATUS_SUCCESS) return null;

                var result = new VideoMemoryStats();

                // 1) Try to read adapter-level ResultAdapter fields at offset 16 (structure defined above)
                try
                {
                    // NodeCount (uint) at offset 16 + 0
                    uint nodeCount = (uint)Marshal.ReadInt32(pBuffer, 16 + 0);
                    // SegmentCount (uint) at offset 16 + 4
                    uint segmentCount = (uint)Marshal.ReadInt32(pBuffer, 16 + 4);
                    // DeviceSharedSystemMemory (ulong) at offset 16 + 8
                    ulong deviceShared = SafeReadUInt64(pBuffer, 16 + 8);
                    // DeviceDedicatedSystemMemory (ulong) at offset 16 + 16
                    ulong deviceDedicated = SafeReadUInt64(pBuffer, 16 + 16);

                    // Accept if at least one makes sense
                    if ((deviceShared > 0 && deviceShared < MaxReasonableBytes()) ||
                        (deviceDedicated > 0 && deviceDedicated < MaxReasonableBytes()))
                    {
                        result.TotalSharedBytes = deviceShared;
                        result.TotalDedicatedBytes = deviceDedicated;
                        // Not necessarily full info, but mark as partially valid
                        result.IsValid = true;
                    }
                }
                catch
                {
                    // ignore
                }

                // 2) Try scanning multiple candidate offsets for a block of plausible ulongs
                // Drivers vary; we look for plausible patterns: (budget, usage, committed, dedicated, shared) or similar
                int[] candidateOffsets = new int[] { 48, 64, 80, 96, 128, 160, 192, 256, 384, 512, 1024 };

                foreach (var off in candidateOffsets)
                {
                    // ensure we don't read beyond buffer
                    if (off + (5 * 8) > bufferSize) continue;

                    try
                    {
                        ulong a = SafeReadUInt64(pBuffer, off + 0);   // candidate budget or total
                        ulong b = SafeReadUInt64(pBuffer, off + 8);   // candidate current usage
                        ulong c = SafeReadUInt64(pBuffer, off + 16);  // candidate committed
                        ulong d = SafeReadUInt64(pBuffer, off + 24);  // candidate dedicated
                        ulong e = SafeReadUInt64(pBuffer, off + 32);  // candidate shared

                        // check plausibility
                        int plausible = 0;
                        if (IsPlausibleMemoryValue(a)) plausible++;
                        if (IsPlausibleMemoryValue(b)) plausible++;
                        if (IsPlausibleMemoryValue(c)) plausible++;
                        if (IsPlausibleMemoryValue(d)) plausible++;
                        if (IsPlausibleMemoryValue(e)) plausible++;

                        // If at least 2 plausible values and at least one is dedicated/shared, accept
                        if (plausible >= 2 && (IsPlausibleMemoryValue(d) || IsPlausibleMemoryValue(e)))
                        {
                            // Map them conservatively
                            if (result.TotalDedicatedBytes == 0 && IsPlausibleMemoryValue(d)) result.TotalDedicatedBytes = d;
                            if (result.TotalSharedBytes == 0 && IsPlausibleMemoryValue(e)) result.TotalSharedBytes = e;
                            if (result.TotalBudgetBytes == 0 && IsPlausibleMemoryValue(a)) result.TotalBudgetBytes = a;
                            if (result.TotalCurrentUsageBytes == 0 && IsPlausibleMemoryValue(b)) result.TotalCurrentUsageBytes = b;
                            if (result.TotalCommittedBytes == 0 && IsPlausibleMemoryValue(c)) result.TotalCommittedBytes = c;

                            result.IsValid = true;
                            // Keep scanning — we might find more complete set further in buffer, but break early if pretty complete
                            if (result.TotalDedicatedBytes > 0 && result.TotalCurrentUsageBytes > 0) break;
                        }
                    }
                    catch
                    {
                        // ignore and continue scanning
                    }
                }

                // 3) If we still don't have totals, attempt to marshal the front structure and use ResultAdapter fallback
                if (!result.IsValid)
                {
                    try
                    {
                        var qstats = Marshal.PtrToStructure<D3DKMT_QUERYSTATISTICS>(pBuffer);
                        var adapterResult = qstats.ResultAdapter;
                        if ((adapterResult.DeviceDedicatedSystemMemory > 0 && adapterResult.DeviceDedicatedSystemMemory < MaxReasonableBytes()) ||
                            (adapterResult.DeviceSharedSystemMemory > 0 && adapterResult.DeviceSharedSystemMemory < MaxReasonableBytes()))
                        {
                            result.TotalDedicatedBytes = adapterResult.DeviceDedicatedSystemMemory;
                            result.TotalSharedBytes = adapterResult.DeviceSharedSystemMemory;
                            result.IsValid = true;
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                }

                // 4) Final plausibility check: ensure values are in reasonable ranges
                if (result.IsValid)
                {
                    // If some values are missing but we have at least a dedicated total, mark valid.
                    if (result.TotalDedicatedBytes == 0 && result.TotalSharedBytes == 0)
                    {
                        // not useful
                        return null;
                    }

                    // If current usage is 0 but dedicated > 0, it's possible (driver doesn't expose usage)
                    // normalize: if TotalCurrentUsage empty, set to 0 (already default)
                    return result;
                }

                return null;
            }
            finally
            {
                if (pBuffer != IntPtr.Zero) Marshal.FreeHGlobal(pBuffer);
            }
        }

        /// <summary>
        /// Try to open the primary adapter (\\.\\DISPLAY1) and return adapter handle and LUID.
        /// The caller can ignore if not required.
        /// </summary>
        public static bool TryGetPrimaryAdapterHandle(out uint adapterHandle, out LUID adapterLuid)
        {
            adapterHandle = 0;
            adapterLuid = new LUID();

            try
            {
                var op = new D3DKMT_OPENADAPTERFROMGDIDISPLAYNAME
                {
                    DeviceName = "\\.\\DISPLAY1",
                    hAdapter = 0,
                    AdapterLuid = new LUID(),
                    VidPnSourceId = 0
                };

                uint status = D3DKMTOpenAdapterFromGdiDisplayName(ref op);
                if (status == STATUS_SUCCESS && op.hAdapter != 0)
                {
                    adapterHandle = op.hAdapter;
                    adapterLuid = op.AdapterLuid;
                    // Close adapter handle as caller likely doesn't need it permanently
                    try { D3DKMTCloseAdapter(ref op.hAdapter); } catch { }
                    return true;
                }
            }
            catch
            {
                // ignore
            }

            return false;
        }

        /// <summary>
        /// High-level wrapper that tries to query the primary adapter video memory stats.
        /// Returns true and fills stats if successful and plausible; otherwise returns false.
        /// </summary>
        public static bool TryQueryPrimaryAdapterVideoMemory(out VideoMemoryStats? stats)
        {
            stats = null;
            try
            {
                // Primary attempt: direct QueryVideoMemoryStatistics
                var s = QueryVideoMemoryStatistics();
                if (s != null && s.IsValid)
                {
                    stats = s;
                    return true;
                }

                // Second attempt: open adapter then query (some drivers require adapter handle / luid present in structure)
                // We'll attempt to fill AdapterLuid in the buffer by calling OpenAdapterFromGdiDisplayName then QueryStatistics.
                if (TryGetPrimaryAdapterHandle(out uint adapterHandle, out LUID adapterLuid))
                {
                    // allocate buffer and set AdapterLuid area (at offset 4...)
                    int bufSize = 8192;
                    IntPtr pBuffer = IntPtr.Zero;
                    try
                    {
                        pBuffer = Marshal.AllocHGlobal(bufSize);
                        for (int i = 0; i < bufSize; i++) Marshal.WriteByte(pBuffer, i, 0);

                        // Set type
                        Marshal.WriteInt32(pBuffer, 0, (int)D3DKMT_QUERYSTATISTICS_VIDEO_MEMORY);

                        // Write AdapterLuid at offset 4 (LowPart:uint, HighPart:int)
                        Marshal.WriteInt32(pBuffer, 4, (int)adapterLuid.LowPart);
                        Marshal.WriteInt32(pBuffer, 8, (int)adapterLuid.HighPart);

                        uint status = D3DKMTQueryStatistics(pBuffer);
                        if (status == STATUS_SUCCESS)
                        {
                            // Try to parse
                            var parsed = ParseVideoMemoryFromBuffer(pBuffer, bufSize);
                            if (parsed != null && parsed.IsValid)
                            {
                                stats = parsed;
                                return true;
                            }
                        }
                    }
                    catch
                    {
                        // ignore and continue
                    }
                    finally
                    {
                        if (pBuffer != IntPtr.Zero) Marshal.FreeHGlobal(pBuffer);
                        try { D3DKMTCloseAdapter(ref adapterHandle); } catch { }
                    }
                }
            }
            catch
            {
                // swallow
            }

            stats = null;
            return false;
        }

        // ---------- Helpers ----------

        private static VideoMemoryStats? ParseVideoMemoryFromBuffer(IntPtr pBuffer, int bufferSize)
        {
            try
            {
                var result = new VideoMemoryStats();

                // 1) adapter fields (offset 16)
                try
                {
                    ulong deviceShared = SafeReadUInt64(pBuffer, 16 + 8);
                    ulong deviceDedicated = SafeReadUInt64(pBuffer, 16 + 16);

                    if (IsPlausibleMemoryValue(deviceShared) || IsPlausibleMemoryValue(deviceDedicated))
                    {
                        result.TotalSharedBytes = deviceShared;
                        result.TotalDedicatedBytes = deviceDedicated;
                        result.IsValid = true;
                    }
                }
                catch { }

                // 2) scan candidate offsets for a group of five ulongs
                int[] candidateOffsets = new int[] { 48, 64, 80, 96, 128, 160, 192, 256, 384, 512, 1024 };

                foreach (var off in candidateOffsets)
                {
                    if (off + (5 * 8) > bufferSize) continue;
                    try
                    {
                        ulong a = SafeReadUInt64(pBuffer, off + 0);
                        ulong b = SafeReadUInt64(pBuffer, off + 8);
                        ulong c = SafeReadUInt64(pBuffer, off + 16);
                        ulong d = SafeReadUInt64(pBuffer, off + 24);
                        ulong e = SafeReadUInt64(pBuffer, off + 32);

                        int plausible = 0;
                        if (IsPlausibleMemoryValue(a)) plausible++;
                        if (IsPlausibleMemoryValue(b)) plausible++;
                        if (IsPlausibleMemoryValue(c)) plausible++;
                        if (IsPlausibleMemoryValue(d)) plausible++;
                        if (IsPlausibleMemoryValue(e)) plausible++;

                        if (plausible >= 2 && (IsPlausibleMemoryValue(d) || IsPlausibleMemoryValue(e)))
                        {
                            if (result.TotalBudgetBytes == 0 && IsPlausibleMemoryValue(a)) result.TotalBudgetBytes = a;
                            if (result.TotalCurrentUsageBytes == 0 && IsPlausibleMemoryValue(b)) result.TotalCurrentUsageBytes = b;
                            if (result.TotalCommittedBytes == 0 && IsPlausibleMemoryValue(c)) result.TotalCommittedBytes = c;
                            if (result.TotalDedicatedBytes == 0 && IsPlausibleMemoryValue(d)) result.TotalDedicatedBytes = d;
                            if (result.TotalSharedBytes == 0 && IsPlausibleMemoryValue(e)) result.TotalSharedBytes = e;

                            result.IsValid = true;
                            // if we have a current usage and dedicated total we can stop
                            if (result.TotalCurrentUsageBytes > 0 && result.TotalDedicatedBytes > 0) break;
                        }
                    }
                    catch { }
                }

                return result.IsValid ? result : null;
            }
            catch { return null; }
        }

        private static ulong SafeReadUInt64(IntPtr basePtr, int offset)
        {
            // Ensure within range - Marshal.ReadInt64 will throw if invalid offset
            // This helper catches exceptions and returns 0 when out-of-range or invalid
            try
            {
                long tmp = Marshal.ReadInt64(basePtr, offset);
                // convert sign-agnostic
                return unchecked((ulong)tmp);
            }
            catch
            {
                return 0;
            }
        }

        private static bool IsPlausibleMemoryValue(ulong v)
        {
            if (v == 0) return false;
            // Reasonable upper bound is e.g. 64 TB (overkill). Use smaller to avoid false positives.
            ulong maxReasonable = MaxReasonableBytes();
            return v > 0 && v < maxReasonable;
        }

        private static ulong MaxReasonableBytes()
        {
            // 16 TB upper bound (plenty)
            return 16UL * 1024UL * 1024UL * 1024UL * 1024UL;
        }
    }
}