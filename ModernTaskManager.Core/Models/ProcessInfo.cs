// ModernTaskManager.Core/Models/ProcessInfo.cs

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ModernTaskManager.Core.Models
{
    public enum ProcessCategory
    {
        Background,
        Application,
        Windows
    }

    public class ProcessInfo : INotifyPropertyChanged
    {
        // -----------------------------
        // CAMPOS PRIVADOS PARA BINDING
        // -----------------------------
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

        // Nuevos campos
        private string _architecture = string.Empty;
        private string _commandLine = string.Empty;
        private IntPtr _iconHandle;

        // -----------------------------
        // PROPIEDADES PÚBLICAS
        // -----------------------------

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

        public IntPtr IconHandle
        {
            get => _iconHandle;
            set => SetProperty(ref _iconHandle, value);
        }

        // Estas propiedades no estaban implementadas con SetProperty antes,
        // si deseas que también notifiquen, puedo agregarlas.
        public int ThreadCount { get; set; }
        public int HandleCount { get; set; }

        public long KernelTime { get; set; }
        public long UserTime { get; set; }

        public long DiskReadBytes { get; set; }
        public long DiskWriteBytes { get; set; }


        // -----------------------------
        // INotifyPropertyChanged
        // -----------------------------

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
        {
            if (Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
