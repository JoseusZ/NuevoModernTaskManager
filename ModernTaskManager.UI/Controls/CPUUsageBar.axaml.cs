using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using System;

namespace ModernTaskManager.UI.Controls
{
    public partial class CPUUsageBar : UserControl
    {
        public static readonly StyledProperty<double> ValueProperty =
            AvaloniaProperty.Register<CPUUsageBar, double>(nameof(Value));

        public double Value
        {
            get => GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public CPUUsageBar()
        {
            InitializeComponent();
            this.GetObservable(ValueProperty).Subscribe(new ValueObserver(this));
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void UpdateVisuals(double val)
        {
            if (ValueText is null || FillBorder is null) return;

            if (Bounds.Width > 0)
                FillBorder.Width = (val / 100.0) * Bounds.Width;
            else
                Dispatcher.UIThread.Post(() => UpdateVisuals(val));

            ValueText.Text = $"{val:F1}%";
            FillBorder.Background = val > 90 ? SolidColorBrush.Parse("#FF5C5C")
                : val > 70 ? SolidColorBrush.Parse("#FFD166")
                : SolidColorBrush.Parse("#0EA5FF");
        }

        private sealed class ValueObserver : IObserver<double>
        {
            private readonly CPUUsageBar _owner;
            public ValueObserver(CPUUsageBar owner) { _owner = owner; }
            public void OnCompleted() { }
            public void OnError(Exception error) { }
            public void OnNext(double value) => _owner.UpdateVisuals(value);
        }
    }
}