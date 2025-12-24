using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using System.Windows.Input;

namespace ModernTaskManager.UI.Views;

public partial class ProcessesView : UserControl
{
    public static readonly DirectProperty<ProcessesView, ICommand?> EndTaskCommandProperty =
        AvaloniaProperty.RegisterDirect<ProcessesView, ICommand?>(
            nameof(EndTaskCommand),
            o => o.EndTaskCommand);

    private ICommand? _endTaskCommand;
    public ICommand? EndTaskCommand
    {
        get => _endTaskCommand;
        private set => SetAndRaise(EndTaskCommandProperty, ref _endTaskCommand, value);
    }

    public ProcessesView()
    {
        InitializeComponent();
        this.DataContextChanged += (_, __) => SyncCommandsFromViewModel();
        SyncCommandsFromViewModel();
    }

    private void SyncCommandsFromViewModel()
    {
        if (DataContext is ModernTaskManager.UI.ViewModels.ProcessesViewModel vm)
        {
            EndTaskCommand = vm.EndTaskCommand;
        }
        else
        {
            EndTaskCommand = null;
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}