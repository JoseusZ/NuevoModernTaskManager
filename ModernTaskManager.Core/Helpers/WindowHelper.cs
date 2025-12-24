// En: ModernTaskManager.Core/Helpers/WindowHelper.cs

using System;
using System.Collections.Generic;
using System.Text;
using ModernTaskManager.Core.Native;

namespace ModernTaskManager.Core.Helpers
{
    public static class WindowHelper
    {
        /// <summary>
        /// Devuelve un diccionario con los PIDs que tienen ventanas visibles ("Aplicaciones").
        /// Clave: PID, Valor: Título de la ventana principal.
        /// </summary>
        public static Dictionary<int, string> GetAppProcesses()
        {
            var apps = new Dictionary<int, string>();
            IntPtr shellWindow = User32.GetShellWindow(); // Ignorar el escritorio/shell

            User32.EnumWindows((hWnd, lParam) =>
            {
                // 1. Filtro: ¿Es visible?
                if (!User32.IsWindowVisible(hWnd)) return true;

                // 2. Filtro: ¿Es la ventana del Shell (Escritorio)?
                if (hWnd == shellWindow) return true;

                // 3. Filtro: ¿Tiene dueño? (Las Apps principales no suelen tener dueño)
                IntPtr owner = User32.GetWindow(hWnd, User32.GW_OWNER);
                if (owner != IntPtr.Zero)
                {
                    // Si tiene dueño, verificamos si el dueño es visible.
                    // Si el dueño es visible, esta es probablemente un diálogo modal, no la App principal.
                    if (User32.IsWindowVisible(owner)) return true;
                }

                // 4. Filtro: ¿Tiene título?
                int length = User32.GetWindowTextLength(hWnd);
                if (length == 0) return true;

                // Obtener el título
                StringBuilder sb = new StringBuilder(length + 1);
                User32.GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();

                // 5. Filtro: Ignorar el "Program Manager"
                if (title == "Program Manager") return true;

                // --- ES UNA APP VÁLIDA ---

                // Obtener el PID de esta ventana
                User32.GetWindowThreadProcessId(hWnd, out uint pid);

                // Si el proceso ya está registrado, quizás esta ventana sea "mejor" o más nueva.
                // Por simplicidad, nos quedamos con la primera que encontramos o sobrescribimos.
                if (!apps.ContainsKey((int)pid))
                {
                    apps.Add((int)pid, title);
                }

                return true; // Continuar enumeración
            }, IntPtr.Zero);

            return apps;
        }
    }
}