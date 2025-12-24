using ModernTaskManager.UI.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace ModernTaskManager.UI.Services
{
    public class MockProcessService
    {
        private Random _rnd = new Random();
        private List<string> _appNames = new List<string> { "Chrome", "Spotify", "Visual Studio", "Discord", "Teams", "Slack", "Explorer" };

        public ObservableCollection<ProcessModel> GenerateInitialData()
        {
            var list = new ObservableCollection<ProcessModel>();

            // Apps
            foreach (var app in _appNames)
            {
                list.Add(CreateRandomProcess(app, false));
            }

            // Background
            for (int i = 0; i < 30; i++)
            {
                list.Add(CreateRandomProcess($"Service Host {i}", true));
            }

            return list;
        }

        private ProcessModel CreateRandomProcess(string name, bool bg)
        {
            return new ProcessModel
            {
                Name = name,
                IsBackground = bg,
                Status = bg ? "Running" : "Suspended",
                CpuPercent = _rnd.NextDouble() * 15,
                MemoryMB = _rnd.NextDouble() * 500,
                DiskMbps = _rnd.NextDouble() * 2,
                NetworkMbps = _rnd.NextDouble() * 5
            };
        }

        // Simula actualización en tiempo real
        public async Task StartSimulation(ObservableCollection<ProcessModel> processes)
        {
            while (true)
            {
                await Task.Delay(1000);
                foreach (var p in processes)
                {
                    // Variación aleatoria
                    double delta = (_rnd.NextDouble() - 0.5) * 5;
                    double newCpu = p.CpuPercent + delta;
                    if (newCpu < 0) newCpu = 0;
                    if (newCpu > 100) newCpu = 100;
                    p.CpuPercent = newCpu;
                }
            }
        }
    }
}