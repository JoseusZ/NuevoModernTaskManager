// En: ModernTaskManager.Core/Services/WindowsServiceManager.cs

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess; // NuGet
using ModernTaskManager.Core.Models;
using ModernTaskManager.Core.Native;

namespace ModernTaskManager.Core.Services
{
    public class WindowsServiceManager
    {
        public List<ServiceInfo> GetServices()
        {
            var services = new List<ServiceInfo>();
            var scServices = ServiceController.GetServices();

            foreach (var sc in scServices)
            {
                var info = new ServiceInfo
                {
                    ServiceName = sc.ServiceName,
                    DisplayName = sc.DisplayName,
                    Status = sc.Status.ToString(),
                    Pid = 0 // Por defecto 0
                };

                // Si está corriendo, intentamos obtener el PID real
                if (sc.Status == ServiceControllerStatus.Running)
                {
                    info.Pid = GetServicePid(sc.ServiceName);
                }

                services.Add(info);
            }

            return services.OrderBy(s => s.ServiceName).ToList();
        }

        private int GetServicePid(string serviceName)
        {
            IntPtr scmHandle = IntPtr.Zero;
            IntPtr svcHandle = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;

            try
            {
                // 1. Abrir el Service Control Manager
                scmHandle = AdvApi32.OpenSCManager(null, null, AdvApi32.SC_MANAGER_CONNECT);
                if (scmHandle == IntPtr.Zero) return 0;

                // 2. Abrir el Servicio específico
                svcHandle = AdvApi32.OpenService(scmHandle, serviceName, AdvApi32.SERVICE_QUERY_STATUS);
                if (svcHandle == IntPtr.Zero) return 0;

                // 3. Consultar el estatus extendido (necesitamos un buffer)
                uint bytesNeeded = 0;

                // Primera llamada para obtener el tamaño necesario (siempre fallará, es normal)
                AdvApi32.QueryServiceStatusEx(svcHandle, AdvApi32.SC_STATUS_PROCESS_INFO, IntPtr.Zero, 0, out bytesNeeded);

                if (bytesNeeded > 0)
                {
                    buffer = Marshal.AllocHGlobal((int)bytesNeeded);

                    // Segunda llamada real
                    if (AdvApi32.QueryServiceStatusEx(svcHandle, AdvApi32.SC_STATUS_PROCESS_INFO, buffer, bytesNeeded, out bytesNeeded))
                    {
                        var ssp = Marshal.PtrToStructure<AdvApi32.SERVICE_STATUS_PROCESS>(buffer);
                        return ssp.dwProcessId;
                    }
                }
            }
            catch
            {
                return 0;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (svcHandle != IntPtr.Zero) AdvApi32.CloseServiceHandle(svcHandle);
                if (scmHandle != IntPtr.Zero) AdvApi32.CloseServiceHandle(scmHandle);
            }

            return 0;
        }

        // ... (Mantén los métodos StartService y StopService que ya tenías) ...
        public void StartService(string serviceName)
        {
            using var sc = new ServiceController(serviceName);
            if (sc.Status != ServiceControllerStatus.Running)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
            }
        }

        public void StopService(string serviceName)
        {
            using var sc = new ServiceController(serviceName);
            if (sc.Status == ServiceControllerStatus.Running)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
            }
        }
    }
}