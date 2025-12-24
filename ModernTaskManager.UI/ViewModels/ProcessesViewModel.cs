using ModernTaskManager.UI.Models;
using ModernTaskManager.UI.Services;
using System;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace ModernTaskManager.UI.ViewModels
{
    public class ProcessesViewModel : ViewModelBase
    {
        private readonly MockProcessService _service;
        private readonly ThemeService _themeService;

        public ObservableCollection<ProcessModel> Processes { get; set; }

        private bool _isDetailsOpen;
        public bool IsDetailsOpen
        {
            get => _isDetailsOpen;
            set { _isDetailsOpen = value; OnPropertyChanged(); }
        }

        public ICommand ToggleDetailsCommand { get; }
        public ICommand EndTaskCommand { get; }

        public ProcessesViewModel()
        {
            _service = new MockProcessService();
            _themeService = new ThemeService();

            Processes = _service.GenerateInitialData();

            // Iniciar simulación (fire and forget para mock)
            _ = _service.StartSimulation(Processes);

            ToggleDetailsCommand = new RelayCommand(_ => IsDetailsOpen = !IsDetailsOpen);
            EndTaskCommand = new RelayCommand(param =>
            {
                if (param is ProcessModel p) Processes.Remove(p);
            });
        }
    }

    // Helper simple para comandos si no usas CommunityToolkit
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        public RelayCommand(Action<object?> execute) => _execute = execute;
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute(parameter);
    }
}