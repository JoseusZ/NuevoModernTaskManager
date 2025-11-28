// En: ModernTaskManager.Core/Models/ProcessInfo.cs

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ModernTaskManager.Core.Helpers; // Para IconHelper

namespace ModernTaskManager.Core.Models
{
    public enum ProcessCategory
    {
        Background,
        Application,
        Windows
    }

    // Agregamos IDisposable para limpiar el icono
    public class ProcessInfo : INotifyPropertyChanged, IDisposable
    {
        private int _pid;
        private string _name = string.Empty;
        private string _username = string.Empty;
        private long _workingSetSize;
        private long _privatePageCount;
        private long _readOperations;
        private long _writeOperations;
        private double _cpuUsage;

        private long _diskReadSpeed;
        private long _diskWriteSpeed;

        private ProcessCategory _category;
        private string _mainWindowTitle = string.Empty;

        private string _architecture = string.Empty;
        private string _commandLine = string.Empty;

        // *** ¡NUEVO! Icono ***
        private IntPtr _iconHandle = IntPtr.Zero;
        public IntPtr IconHandle
        {
            get => _iconHandle;
            set => SetProperty(ref _iconHandle, value);
        }

        public string Architecture
        {
            get => _architecture;
            set => SetProperty(ref _architecture, value);
        }

        public string CommandLine
        {
            get => _commandLine;
            set => SetProperty(ref _commandLine, value);
        }

        public ProcessCategory Category
        {
            get => _category;
            set => SetProperty(ref _category, value);
        }

        public string MainWindowTitle
        {
            get => _mainWindowTitle;
            set => SetProperty(ref _mainWindowTitle, value);
        }

        public int Pid
        {
            get => _pid;
            set => SetProperty(ref _pid, value);
        }

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public string Username
        {
            get => _username;
            set => SetProperty(ref _username, value);
        }

        public long WorkingSetSize
        {
            get => _workingSetSize;
            set => SetProperty(ref _workingSetSize, value);
        }

        public long PrivatePageCount
        {
            get => _privatePageCount;
            set => SetProperty(ref _privatePageCount, value);
        }

        public long ReadOperations
        {
            get => _readOperations;
            set => SetProperty(ref _readOperations, value);
        }

        public long WriteOperations
        {
            get => _writeOperations;
            set => SetProperty(ref _writeOperations, value);
        }

        public double CpuUsage
        {
            get => _cpuUsage;
            set => SetProperty(ref _cpuUsage, value);
        }

        public long DiskReadSpeed
        {
            get => _diskReadSpeed;
            set => SetProperty(ref _diskReadSpeed, value);
        }

        public long DiskWriteSpeed
        {
            get => _diskWriteSpeed;
            set => SetProperty(ref _diskWriteSpeed, value);
        }

        public int ThreadCount { get; set; }
        public int HandleCount { get; set; }

        public long KernelTime { get; set; }
        public long UserTime { get; set; }

        public long DiskReadBytes { get; set; }
        public long DiskWriteBytes { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = "")
        {
            if (Equals(storage, value)) return false;
            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        // Limpieza de recursos nativos (Icono)
        public void Dispose()
        {
            if (_iconHandle != IntPtr.Zero)
            {
                IconHelper.DestroyIconSafe(_iconHandle);
                _iconHandle = IntPtr.Zero;
            }
            GC.SuppressFinalize(this);
        }
    }
}