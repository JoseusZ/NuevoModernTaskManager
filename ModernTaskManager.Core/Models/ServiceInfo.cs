namespace ModernTaskManager.Core.Models
{
    public class ServiceInfo
    {
        public string ServiceName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty; // "Running", "Stopped"
        public int Pid { get; set; } // 0 si está detenido
        public string Description { get; set; } = string.Empty;
    }
}