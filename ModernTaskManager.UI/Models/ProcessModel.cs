using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ModernTaskManager.UI.Models
{
    public class ProcessModel : INotifyPropertyChanged
    {
        private double _cpuPercent;
        private double _memoryMB;
        private double _diskMbps;
        private double _networkMbps;
        private string _status;

        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string IconPath { get; set; } = ""; // Ruta o key de recurso
        public bool IsBackground { get; set; }

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public double CpuPercent
        {
            get => _cpuPercent;
            set { _cpuPercent = value; OnPropertyChanged(); }
        }

        public double MemoryMB
        {
            get => _memoryMB;
            set { _memoryMB = value; OnPropertyChanged(); }
        }

        public double DiskMbps
        {
            get => _diskMbps;
            set { _diskMbps = value; OnPropertyChanged(); }
        }

        public double NetworkMbps
        {
            get => _networkMbps;
            set { _networkMbps = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}