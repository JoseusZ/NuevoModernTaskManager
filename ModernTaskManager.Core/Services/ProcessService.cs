// ModernTaskManager.Core/Services/ProcessService.cs

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using ModernTaskManager.Core.Models;
using ModernTaskManager.Core.Native;

namespace ModernTaskManager.Core.Services
{
    public class ProcessService : IDisposable
    {
        private bool _disposed = false;

        /// <summary>
        /// Obtiene una lista actualizada de todos los procesos del sistema
        /// usando la API nativa NtQuerySystemInformation.
        /// </summary>
        public List<ProcessInfo> GetProcesses()
        {
            var processes = new List<ProcessInfo>();
            uint bufferSize = 0;
            IntPtr buffer = IntPtr.Zero;
            uint status;

            try
            {
                // 1. Obtener tamaño del buffer necesario
                NtDll.NtQuerySystemInformation(NtDll.SystemProcessInformation, IntPtr.Zero, 0, out bufferSize);

                do
                {
                    // Liberar buffer anterior si existe
                    if (buffer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(buffer);
                        buffer = IntPtr.Zero;
                    }

                    // Asignar memoria nativa
                    buffer = Marshal.AllocHGlobal((int)bufferSize);

                    // 2. Llamada real para obtener datos
                    status = NtDll.NtQuerySystemInformation(NtDll.SystemProcessInformation, buffer, bufferSize, out bufferSize);

                } while (status == NtDll.STATUS_INFO_LENGTH_MISMATCH);

                if (status != 0)
                {
                    throw new Exception($"NtQuerySystemInformation falló con estado: 0x{status:X}");
                }

                // 3. Iterar sobre el buffer
                IntPtr currentPtr = buffer;
                do
                {
                    var spi = Marshal.PtrToStructure<NtDll.SYSTEM_PROCESS_INFORMATION>(currentPtr);

                    var pi = new ProcessInfo
                    {
                        Pid = spi.UniqueProcessId.ToInt32(),
                        WorkingSetSize = spi.WorkingSetSize.ToInt64(),
                        PrivatePageCount = spi.PrivatePageCount.ToInt64(),
                        ReadOperations = spi.ReadOperationCount,
                        WriteOperations = spi.WriteOperationCount,

                        // Bytes de Disco (Acumulado)
                        DiskReadBytes = spi.ReadTransferCount,
                        DiskWriteBytes = spi.WriteTransferCount,

                        KernelTime = spi.KernelTime,
                        UserTime = spi.UserTime,

                        // Estadísticas
                        ThreadCount = (int)spi.NumberOfThreads,
                        HandleCount = (int)spi.HandleCount
                    };

                    // Obtener nombre del proceso
                    if (spi.ImageName.Length > 0 && spi.ImageName.Buffer != IntPtr.Zero)
                    {
                        pi.Name = Marshal.PtrToStringUni(spi.ImageName.Buffer, spi.ImageName.Length / 2) ?? "Unknown";
                    }
                    else
                    {
                        pi.Name = spi.UniqueProcessId.ToInt32() == 0 ? "Idle" : "System";
                    }

                    // Obtener nombre de usuario
                    pi.Username = GetProcessUsername(pi.Pid);

                    // Solo añadir procesos válidos
                    if (pi.Pid != 0)
                    {
                        processes.Add(pi);
                    }

                    // Fin de la lista
                    if (spi.NextEntryOffset == 0)
                        break;

                    // Siguiente entrada
                    currentPtr = new IntPtr(currentPtr.ToInt64() + spi.NextEntryOffset);

                } while (true);
            }
            finally
            {
                // 4. Liberar memoria
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return processes;
        }

        /// <summary>
        /// Obtiene el nombre de usuario de un proceso dado su PID.
        /// </summary>
        private static string GetProcessUsername(int pid)
        {
            IntPtr processHandle = IntPtr.Zero;
            IntPtr tokenHandle = IntPtr.Zero;
            IntPtr tokenInfo = IntPtr.Zero;

            try
            {
                // 1. Abrir el proceso
                processHandle = Kernel32.OpenProcess(Kernel32.ProcessAccessFlags.QueryLimitedInformation, false, pid);
                if (processHandle == IntPtr.Zero)
                {
                    return "N/A";
                }

                // 2. Abrir el token del proceso
                if (!AdvApi32.OpenProcessToken(processHandle, AdvApi32.TokenAccessFlags.Query, out tokenHandle))
                {
                    return "N/A";
                }

                // 3. Obtener tamaño del buffer
                if (!AdvApi32.GetTokenInformation(tokenHandle, AdvApi32.TOKEN_INFORMATION_CLASS.TokenUser, IntPtr.Zero, 0, out uint returnLength) && returnLength == 0)
                {
                    return "N/A";
                }

                // 4. Asignar memoria y obtener info
                tokenInfo = Marshal.AllocHGlobal((int)returnLength);
                if (!AdvApi32.GetTokenInformation(tokenHandle, AdvApi32.TOKEN_INFORMATION_CLASS.TokenUser, tokenInfo, returnLength, out returnLength))
                {
                    return "N/A";
                }

                // 5. Mapear estructura
                var tokenUser = Marshal.PtrToStructure<AdvApi32.TOKEN_USER>(tokenInfo);

                // 6. Traducir SID a nombre
                uint nameSize = 0;
                uint domainSize = 0;
                _ = AdvApi32.LookupAccountSid(null, tokenUser.User.Sid, null!, ref nameSize, null!, ref domainSize, out _);

                var name = new StringBuilder((int)nameSize);
                var domain = new StringBuilder((int)domainSize);

                if (!AdvApi32.LookupAccountSid(null, tokenUser.User.Sid, name, ref nameSize, domain, ref domainSize, out _))
                {
                    return "N/A";
                }

                return $"{domain}\\{name}";
            }
            catch
            {
                return "N/A";
            }
            finally
            {
                // Liberar recursos
                if (tokenInfo != IntPtr.Zero) Marshal.FreeHGlobal(tokenInfo);
                if (tokenHandle != IntPtr.Zero) _ = Kernel32.CloseHandle(tokenHandle);
                if (processHandle != IntPtr.Zero) _ = Kernel32.CloseHandle(processHandle);
            }
        }

        // ---------------------------------------------------------
        // ACCIONES DE CONTROL DE PROCESOS
        // ---------------------------------------------------------

        /// <summary>
        /// Termina (mata) un proceso forzosamente.
        /// </summary>
        public void KillProcess(int pid)
        {
            IntPtr hProcess = IntPtr.Zero;
            try
            {
                // Abrir con permiso TERMINATE (0x0001)
                hProcess = Kernel32.OpenProcess(Kernel32.ProcessAccessFlags.Terminate, false, pid);

                if (hProcess == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new Exception($"No se pudo abrir el proceso {pid} (Acceso denegado o proceso no existe). Error: {error}");
                }

                // Terminar con código de salida 1
                if (!Kernel32.TerminateProcess(hProcess, 1))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new Exception($"Falló al terminar el proceso. Error Win32: {error}");
                }
            }
            finally
            {
                if (hProcess != IntPtr.Zero) Kernel32.CloseHandle(hProcess);
            }
        }

        /// <summary>
        /// Cambia la prioridad del proceso.
        /// </summary>
        public void SetPriority(int pid, uint priorityClass)
        {
            IntPtr hProcess = IntPtr.Zero;
            try
            {
                // Abrir con permiso SET_INFORMATION (0x0200)
                hProcess = Kernel32.OpenProcess(Kernel32.ProcessAccessFlags.SetInformation, false, pid);

                if (hProcess == IntPtr.Zero)
                    throw new Exception("No se pudo abrir el proceso para cambiar prioridad. ¿Acceso denegado?");

                if (!Kernel32.SetPriorityClass(hProcess, priorityClass))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new Exception($"No se pudo cambiar la prioridad. Error Win32: {error}");
                }
            }
            finally
            {
                if (hProcess != IntPtr.Zero) Kernel32.CloseHandle(hProcess);
            }
        }

        /// <summary>
        /// Establece la afinidad de CPU (qué núcleos puede usar el proceso).
        /// </summary>
        public void SetAffinity(int pid, IntPtr affinityMask)
        {
            IntPtr hProcess = IntPtr.Zero;
            try
            {
                hProcess = Kernel32.OpenProcess(Kernel32.ProcessAccessFlags.SetInformation, false, pid);

                if (hProcess == IntPtr.Zero)
                    throw new Exception("No se pudo abrir el proceso para cambiar afinidad.");

                if (!Kernel32.SetProcessAffinityMask(hProcess, affinityMask))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new Exception($"No se pudo establecer la afinidad. Error Win32: {error}");
                }
            }
            finally
            {
                if (hProcess != IntPtr.Zero) Kernel32.CloseHandle(hProcess);
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }
    }
}