// ModernTaskManager.Core/Native/DXGIWrapper.cs
// Safe, defensive DXGI wrapper that avoids AccessViolation by invoking only a minimal set of COM methods
// and using function-pointer delegates to call vtable entries directly.
// Provides: DedicatedVideoMemory and SharedSystemMemory reliably (GetDesc).
//
// ADDED: Safe IDXGIAdapter3 implementation that works alongside existing code without breaking anything
//
using ModernTaskManager.Core.Helpers;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ModernTaskManager.Core.Native
{
    public static class DXGIWrapper
    {
        private const int S_OK = 0;
        private static readonly Guid IID_IDXGIFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        private static readonly Guid IID_IDXGIAdapter3 = new Guid("645967A4-1392-4310-A798-8053CE3E93FD");

        [StructLayout(LayoutKind.Sequential)]
        public struct LARGE_INTEGER
        {
            public long QuadPart;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DXGI_ADAPTER_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public ulong DedicatedVideoMemory;
            public ulong DedicatedSystemMemory;
            public ulong SharedSystemMemory;
            public LARGE_INTEGER AdapterLuid;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DXGI_QUERY_VIDEO_MEMORY_INFO
        {
            public ulong Budget;
            public ulong CurrentUsage;
            public ulong AvailableForReservation;
            public ulong CurrentReservation;
        }

        [DllImport("dxgi.dll", ExactSpelling = true)]
        private static extern int CreateDXGIFactory1([In] ref Guid riid, out IntPtr ppFactory);

        // Delegates for vtable calls (we only implement what we need)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumAdapters1Delegate(IntPtr thisPtr, uint index, out IntPtr ppAdapter);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetDescDelegate(IntPtr thisPtr, out DXGI_ADAPTER_DESC desc);

        // NEW: Safe delegate for IDXGIAdapter3 - ONLY used in separate method
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int QueryVideoMemoryInfoDelegate(IntPtr thisPtr, uint nodeIndex, int memorySegmentGroup, out DXGI_QUERY_VIDEO_MEMORY_INFO memoryInfo);

        public class DXGIResult
        {
            public ulong DedicatedVideoMemory { get; set; }
            public ulong SharedSystemMemory { get; set; }
            public ulong Budget { get; set; } = 0;
            public ulong CurrentUsage { get; set; } = 0;
            public bool Success { get; set; }
            public string AdapterDescription { get; set; }
        }

        /// <summary>
        /// Query basic video memory info using DXGI in a safe manner.
        /// Returns dedicated/shared memory and adapter description reliably.
        /// Budget/CurrentUsage remain 0 unless a safe method to obtain them is implemented.
        /// </summary>
        public static DXGIResult QueryVideoMemoryInfo()
        {
            var result = new DXGIResult { Success = false };

            // DXGI available starting roughly Windows 7 with WDDM, but QueryVideoMemoryInfo (Adapter3) is not universally available.
            if (!WindowsVersion.IsWindows8OrGreater)
            {
                // For Windows 7 we won't try DXGI advanced features.
                result.Success = false;
                return result;
            }

            IntPtr factoryPtr = IntPtr.Zero;
            try
            {
                Guid factoryGuid = IID_IDXGIFactory1;
                int hr = CreateDXGIFactory1(ref factoryGuid, out factoryPtr);
                if (hr != S_OK || factoryPtr == IntPtr.Zero)
                {
                    return result;
                }


                // Get pointer to vtable
                IntPtr vtbl = Marshal.ReadIntPtr(factoryPtr);
                // EnumAdapters1 is typically at vtable index 12 for IDXGIFactory1 (IUnknown 0..2, IDXGIObject 3..6, IDXGIFactory 7..11, IDXGIFactory1 EnumAdapters1 -> 12)
                const int IDXGIFactory1_EnumAdapters1_Index = 12;
                IntPtr enumAdapters1Ptr = Marshal.ReadIntPtr(vtbl, IntPtr.Size * IDXGIFactory1_EnumAdapters1_Index);

                var enumAdapters1 = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(enumAdapters1Ptr);

                // Enumerate adapters (limit reasonably)
                for (uint i = 0; i < 10; i++)
                {
                    IntPtr adapterPtr = IntPtr.Zero;
                    try
                    {
                        int er = enumAdapters1(factoryPtr, i, out adapterPtr);
                        if (er != S_OK || adapterPtr == IntPtr.Zero)
                        {
                            // no more adapters
                            break;
                        }

                        // For the adapter, call GetDesc (vtable index for GetDesc is usually 8: IUnknown(0..2)=0..2, IDXGIObject(3..6)=3..6, EnumOutputs=7, GetDesc=8)
                        IntPtr adapterVtbl = Marshal.ReadIntPtr(adapterPtr);
                        const int IDXGIAdapter_GetDesc_Index = 8;
                        IntPtr getDescPtr = Marshal.ReadIntPtr(adapterVtbl, IntPtr.Size * IDXGIAdapter_GetDesc_Index);

                        var getDesc = Marshal.GetDelegateForFunctionPointer<GetDescDelegate>(getDescPtr);

                        DXGI_ADAPTER_DESC desc;
                        int descHr = getDesc(adapterPtr, out desc);
                        if (descHr == S_OK)
                        {
                            result.DedicatedVideoMemory = desc.DedicatedVideoMemory;
                            result.SharedSystemMemory = desc.SharedSystemMemory;
                            result.AdapterDescription = desc.Description ?? string.Empty;
                            result.Success = true;

                            // NEW: Safe call to get Budget/CurrentUsage WITHOUT breaking existing functionality
                            if (WindowsVersion.IsWindows10OrGreater)
                            {
                                TryGetBudgetAndUsageSafe(adapterPtr, ref result);
                            }

                            return result;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"DXGI adapter handling exception: {ex.Message}");
                        // continue to next adapter
                    }
                    finally
                    {
                        if (adapterPtr != IntPtr.Zero)
                        {
                            try { Marshal.Release(adapterPtr); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DXGIWrapper.QueryVideoMemoryInfo error: {ex.Message}");
            }
            finally
            {
                if (factoryPtr != IntPtr.Zero)
                {
                    try { Marshal.Release(factoryPtr); } catch { }
                }
            }

            return result;
        }

        /// <summary>
        /// NEW: Safe method to get Budget/CurrentUsage without breaking existing code
        /// This is completely isolated and won't affect the main functionality
        /// </summary>
        private static void TryGetBudgetAndUsageSafe(IntPtr adapterPtr, ref DXGIResult result)
        {
            IntPtr adapter3Ptr = IntPtr.Zero;
            
            try
            {
                // Safely query for IDXGIAdapter3 interface
                // Use a local Guid variable so we don't pass a readonly static field by ref (CS0199)
                Guid iidLocal = IID_IDXGIAdapter3;
                int queryResult = Marshal.QueryInterface(adapterPtr, ref iidLocal, out adapter3Ptr);
                if (queryResult != S_OK || adapter3Ptr == IntPtr.Zero)
                {
                    return; // IDXGIAdapter3 not available - silent fail
                }

                // Get the vtable for IDXGIAdapter3
                IntPtr adapter3Vtbl = Marshal.ReadIntPtr(adapter3Ptr);
                
                // QueryVideoMemoryInfo is at index 13 in IDXGIAdapter3 vtable
                const int IDXGIAdapter3_QueryVideoMemoryInfo_Index = 13;
                IntPtr queryVideoMemoryInfoPtr = Marshal.ReadIntPtr(adapter3Vtbl, IntPtr.Size * IDXGIAdapter3_QueryVideoMemoryInfo_Index);
                
                var queryVideoMemoryInfo = Marshal.GetDelegateForFunctionPointer<QueryVideoMemoryInfoDelegate>(queryVideoMemoryInfoPtr);

                // Query for local video memory (dedicated VRAM)
                DXGI_QUERY_VIDEO_MEMORY_INFO memoryInfo;
                int memoryResult = queryVideoMemoryInfo(adapter3Ptr, 0, 0, out memoryInfo);
                
                if (memoryResult == S_OK)
                {
                    result.Budget = memoryInfo.Budget;
                    result.CurrentUsage = memoryInfo.CurrentUsage;
                    Debug.WriteLine($"✅ IDXGIAdapter3 Success: Budget={FormatBytes(memoryInfo.Budget)}, Usage={FormatBytes(memoryInfo.CurrentUsage)}");
                }
            }
            catch (Exception ex)
            {
                // Silent catch - don't break the main functionality
                Debug.WriteLine($"IDXGIAdapter3 safe fallback: {ex.Message}");
            }
            finally
            {
                if (adapter3Ptr != IntPtr.Zero)
                {
                    try { Marshal.Release(adapter3Ptr); } catch { }
                }
            }
        }

        /// <summary>
        /// NEW: Separate method to check if Budget/CurrentUsage are available
        /// This doesn't affect the main QueryVideoMemoryInfo method
        /// </summary>
        public static bool IsBudgetUsageAvailable()
        {
            try
            {
                if (!WindowsVersion.IsWindows10OrGreater)
                    return false;

                var result = QueryVideoMemoryInfo();
                return result.Success && result.Budget > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// NEW: Get only basic info without Budget/CurrentUsage for maximum compatibility
        /// </summary>
        public static DXGIResult QueryBasicVideoMemoryInfo()
        {
            var result = new DXGIResult { Success = false };

            if (!WindowsVersion.IsWindows8OrGreater)
            {
                result.Success = false;
                return result;
            }

            IntPtr factoryPtr = IntPtr.Zero;
            try
            {
                Guid factoryGuid = IID_IDXGIFactory1;
                int hr = CreateDXGIFactory1(ref factoryGuid, out factoryPtr);
                if (hr != S_OK || factoryPtr == IntPtr.Zero)
                {
                    return result;
                }

                IntPtr vtbl = Marshal.ReadIntPtr(factoryPtr);
                const int IDXGIFactory1_EnumAdapters1_Index = 12;
                IntPtr enumAdapters1Ptr = Marshal.ReadIntPtr(vtbl, IntPtr.Size * IDXGIFactory1_EnumAdapters1_Index);
                var enumAdapters1 = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(enumAdapters1Ptr);

                for (uint i = 0; i < 10; i++)
                {
                    IntPtr adapterPtr = IntPtr.Zero;
                    try
                    {
                        int er = enumAdapters1(factoryPtr, i, out adapterPtr);
                        if (er != S_OK || adapterPtr == IntPtr.Zero)
                        {
                            break;
                        }

                        IntPtr adapterVtbl = Marshal.ReadIntPtr(adapterPtr);
                        const int IDXGIAdapter_GetDesc_Index = 8;
                        IntPtr getDescPtr = Marshal.ReadIntPtr(adapterVtbl, IntPtr.Size * IDXGIAdapter_GetDesc_Index);
                        var getDesc = Marshal.GetDelegateForFunctionPointer<GetDescDelegate>(getDescPtr);

                        DXGI_ADAPTER_DESC desc;
                        int descHr = getDesc(adapterPtr, out desc);
                        if (descHr == S_OK)
                        {
                            result.DedicatedVideoMemory = desc.DedicatedVideoMemory;
                            result.SharedSystemMemory = desc.SharedSystemMemory;
                            result.AdapterDescription = desc.Description ?? string.Empty;
                            result.Success = true;
                            return result;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"QueryBasicVideoMemoryInfo adapter exception: {ex.Message}");
                    }
                    finally
                    {
                        if (adapterPtr != IntPtr.Zero)
                        {
                            try { Marshal.Release(adapterPtr); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"QueryBasicVideoMemoryInfo error: {ex.Message}");
            }
            finally
            {
                if (factoryPtr != IntPtr.Zero)
                {
                    try { Marshal.Release(factoryPtr); } catch { }
                }
            }

            return result;
        }

        public static bool IsDXGIAvailable()
        {
            try
            {
                var r = QueryVideoMemoryInfo();
                return r != null && r.Success && (r.DedicatedVideoMemory > 0 || r.SharedSystemMemory > 0);
            }
            catch
            {
                return false;
            }
        }

        // Helper method for formatting
        private static string FormatBytes(ulong bytes)
        {
            if (bytes == 0) return "0 B";
            string[] suffixes = { "B", "KB", "MB", "GB" };
            int counter = 0;
            decimal number = (decimal)bytes;
            
            while (Math.Round(number / 1024) >= 1 && counter < suffixes.Length - 1)
            {
                number /= 1024;
                counter++;
            }
            
            return $"{number:n1} {suffixes[counter]}";
        }
    }
}