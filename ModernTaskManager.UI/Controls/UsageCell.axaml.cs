using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ModernTaskManager.UI.Controls
{
    public partial class UsageCell : UserControl
    {
        public static readonly StyledProperty<double> ValueProperty =
            AvaloniaProperty.Register<UsageCell, double>(nameof(Value));

        public static readonly StyledProperty<string> TextSuffixProperty =
            AvaloniaProperty.Register<UsageCell, string>(nameof(TextSuffix), defaultValue: "%");

        public double Value
        {
            get => GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public string TextSuffix
        {
            get => GetValue(TextSuffixProperty);
            set => SetValue(TextSuffixProperty, value);
        }

        public UsageCell()
        {
            InitializeComponent();
            // Subscribe using an IObserver<double> to avoid CS1503
            this.GetObservable(ValueProperty).Subscribe(new ValueObserver(this));
        }

        private void UpdateVisuals(double val)
        {
            if (ValueText == null || CellBackground == null) return;

            // Formato de texto
            ValueText.Text = $"{val:F1}{TextSuffix}";

            // Lógica de color de fondo (Heatmap simplificado)
            // Win11 usa opacidad: más uso = color más intenso
            Color baseColor;

            if (val > 90) baseColor = Color.Parse("#FF5C5C"); // Rojo Crítico
            else if (val > 50) baseColor = Color.Parse("#FFD166"); // Amarillo/Naranja Warning
            else if (val > 0.1) baseColor = Color.Parse("#0078D4"); // Azul Normal (Opcional, a veces es transparente)
            else
            {
                CellBackground.Background = Brushes.Transparent;
                return;
            }

            // Calculamos opacidad basada en el valor (max 40% opacidad)
            double opacity = (val / 100.0) * 0.4 + 0.1;
            if (opacity > 0.6) opacity = 0.6; // Tope de intensidad

            var color = Color.FromUInt32((uint)(opacity * 255) << 24 | (uint)baseColor.R << 16 | (uint)baseColor.G << 8 | (uint)baseColor.B);
            CellBackground.Background = new SolidColorBrush(color);
        }

        private sealed class ValueObserver : System.IObserver<double>
        {
            private readonly UsageCell _owner;
            public ValueObserver(UsageCell owner) { _owner = owner; }
            public void OnCompleted() { }
            public void OnError(System.Exception error) { }
            public void OnNext(double value) => _owner.UpdateVisuals(value);
        }
    }
}