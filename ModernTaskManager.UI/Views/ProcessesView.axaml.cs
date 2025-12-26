using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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

    private ScrollViewer? _scrollViewer;
    
    // Multiplicador de sensibilidad del scroll (2.5x más rápido)
    private const double ScrollSensitivityMultiplier = 2.5;

    public ProcessesView()
    {
        InitializeComponent();
        this.DataContextChanged += (_, __) => SyncCommandsFromViewModel();
        SyncCommandsFromViewModel();
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        
        // Buscar el ScrollViewer y configurar el manejador de scroll
        _scrollViewer = this.FindControl<ScrollViewer>("ProcessScrollViewer");
        if (_scrollViewer != null)
        {
            _scrollViewer.PointerWheelChanged += OnScrollViewerPointerWheelChanged;
        }
    }

    protected override void OnUnloaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        
        if (_scrollViewer != null)
        {
            _scrollViewer.PointerWheelChanged -= OnScrollViewerPointerWheelChanged;
        }
    }

    private void OnScrollViewerPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_scrollViewer == null) return;

        // Calcular el nuevo offset con mayor sensibilidad
        double deltaY = e.Delta.Y * 50 * ScrollSensitivityMultiplier; // 50 es el valor base por línea
        double newOffset = _scrollViewer.Offset.Y - deltaY;
        
        // Limitar el offset a los límites válidos
        newOffset = System.Math.Max(0, System.Math.Min(newOffset, _scrollViewer.ScrollBarMaximum.Y));
        
        _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X, newOffset);
        
        // Marcar el evento como manejado para evitar el scroll por defecto
        e.Handled = true;
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