using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using System.Windows.Input;

namespace ModernTaskManager.UI.Views.Controls
{
    public partial class ProcessRow : UserControl
    {
        // Definimos la propiedad para que el XAML pueda bindear el comando
        public static readonly StyledProperty<ICommand?> EndTaskCommandProperty =
            AvaloniaProperty.Register<ProcessRow, ICommand?>(nameof(EndTaskCommand));

        public ICommand? EndTaskCommand
        {
            get => GetValue(EndTaskCommandProperty);
            set => SetValue(EndTaskCommandProperty, value);
        }

        public ProcessRow()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}