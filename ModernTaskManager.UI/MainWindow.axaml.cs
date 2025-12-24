using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using ModernTaskManager.UI.Services;

namespace ModernTaskManager.UI;

public partial class MainWindow : Window
{
    private Border? _clipHost;
    private Border? _mainBackground;
    private Border? _windowBorder;
    private PathIcon? _maxIcon;
    private PathIcon? _restoreIcon;
    private ContentControl? _mainContent;
    private Geometry? _roundedClip;

    private MenuItem? _ctxRestore;
    private MenuItem? _ctxMinimize;
    private MenuItem? _ctxMaximize;
    private MenuItem? _ctxClose;

    private Panel? _titleBar;

    public MainWindow()
    {
        InitializeComponent();

        // Búsqueda segura de controles
        _clipHost = this.FindControl<Border>("WindowClipHost");
        _mainBackground = this.FindControl<Border>("MainBackground");
        _windowBorder = this.FindControl<Border>("WindowBorder");
        _maxIcon = this.FindControl<PathIcon>("MaxIcon");
        _restoreIcon = this.FindControl<PathIcon>("RestoreIcon");
        _mainContent = this.FindControl<ContentControl>("MainContent");
        _titleBar = this.FindControl<Panel>("TitleBarDragArea");

        _ctxRestore = this.FindControl<MenuItem>("ContextRestore");
        _ctxMinimize = this.FindControl<MenuItem>("ContextMinimize");
        _ctxMaximize = this.FindControl<MenuItem>("ContextMaximize");
        _ctxClose = this.FindControl<MenuItem>("ContextClose");

        // Wiring de los items del menú de contexto
        if (_ctxRestore != null) _ctxRestore.Click += (_, _) => WindowState = WindowState.Normal;
        if (_ctxMinimize != null) _ctxMinimize.Click += (_, _) => WindowState = WindowState.Minimized;
        if (_ctxMaximize != null) _ctxMaximize.Click += (_, _) => WindowState = WindowState.Maximized;
        if (_ctxClose != null) _ctxClose.Click += (_, _) => Close();

        // Hook para actualizar el menú antes de abrirse
        if (_titleBar?.ContextMenu != null)
        {
            _titleBar.ContextMenu.Opening += OnContextMenuOpening;
        }

        // Backdrop
        BackdropService.Apply(this, _mainBackground);

        // Tema
        this.ActualThemeVariantChanged += OnThemeVariantChanged;

        // Recalcular clip cuando cambia el tamaño
        if (_clipHost != null)
        {
            _clipHost.PropertyChanged += (_, e) =>
            {
                if (e.Property == BoundsProperty)
                    ApplyCornerClip();
            };
        }

        if (_mainContent != null)
            _mainContent.Content = new Views.ProcessesView();

        // Drag y doble-clic en la barra de título (solo botón izquierdo)
        if (_titleBar != null)
        {
            _titleBar.PointerPressed += OnTitleBarPointerPressed;
        }

        // Botones
        var minBtn = this.FindControl<Button>("MinimizeBtn");
        if (minBtn != null)
            minBtn.Click += (_, _) => WindowState = WindowState.Minimized;

        var closeBtn = this.FindControl<Button>("CloseBtn");
        if (closeBtn != null)
            closeBtn.Click += (_, _) => Close();

        var maxRestoreBtn = this.FindControl<Button>("MaximizeRestoreBtn");
        if (maxRestoreBtn != null)
            maxRestoreBtn.Click += ToggleWindowState;

        // Resize
        SetupResizeDrag("ResizeTop", WindowEdge.North);
        SetupResizeDrag("ResizeBottom", WindowEdge.South);
        SetupResizeDrag("ResizeLeft", WindowEdge.West);
        SetupResizeDrag("ResizeRight", WindowEdge.East);
        SetupResizeDrag("ResizeTopLeft", WindowEdge.NorthWest);
        SetupResizeDrag("ResizeTopRight", WindowEdge.NorthEast);
        SetupResizeDrag("ResizeBottomLeft", WindowEdge.SouthWest);
        SetupResizeDrag("ResizeBottomRight", WindowEdge.SouthEast);

        PropertyChanged += MainWindow_PropertyChanged;

        // Estado visual inicial
        Loaded += (_, _) => UpdateVisualState();
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e)
    {
        BackdropService.Apply(this, _mainBackground);
        ApplyCornerClip();
    }

    private void SetupResizeDrag(string controlName, WindowEdge edge)
    {
        var control = this.FindControl<Border>(controlName);
        if (control == null) return;

        control.PointerPressed += (_, e) =>
        {
            if (WindowState != WindowState.Maximized)
                BeginResizeDrag(edge, e);
        };
    }

    private void ToggleWindowState(object? sender, RoutedEventArgs? e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void MainWindow_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty)
            UpdateVisualState();
    }

    private void UpdateVisualState()
    {
        if (_mainBackground == null || _maxIcon == null || _restoreIcon == null)
            return;

        bool isMaximized = WindowState == WindowState.Maximized;

        _maxIcon.IsVisible = !isMaximized;
        _restoreIcon.IsVisible = isMaximized;

        var closeBtn = this.FindControl<Button>("CloseBtn");
        var sidebar = this.FindControl<Border>("SidebarBackground");
        var content = this.FindControl<Border>("ContentBackground");

        CornerRadius chromeRadius = GetWindowCornerRadius();

        if (isMaximized)
        {
            if (_clipHost != null)
                _clipHost.CornerRadius = new CornerRadius(0);

            _mainBackground.CornerRadius = new CornerRadius(0);
            _roundedClip = null;
            ApplyCornerClip();

            if (sidebar != null) sidebar.CornerRadius = new CornerRadius(0);
            if (content != null) content.CornerRadius = new CornerRadius(8, 0, 0, 0);

            if (_windowBorder != null)
            {
                _windowBorder.CornerRadius = new CornerRadius(0);
                _windowBorder.BorderThickness = new Thickness(0);
            }

            closeBtn?.Classes.Add("maximized");
        }
        else
        {
            if (_clipHost != null)
                _clipHost.CornerRadius = chromeRadius;

            _mainBackground.CornerRadius = chromeRadius;
            ApplyCornerClip();

            if (sidebar != null)
                sidebar.CornerRadius = new CornerRadius(0, 0, 0, chromeRadius.BottomLeft);

            if (content != null)
                content.CornerRadius = new CornerRadius(8, 0, chromeRadius.BottomRight, 0);

            if (_windowBorder != null)
            {
                _windowBorder.CornerRadius = chromeRadius;
                // Mostrar borde en todas las versiones cuando no está maximizado
                _windowBorder.BorderThickness = new Thickness(1);
            }

            closeBtn?.Classes.Remove("maximized");
        }
    }

    private CornerRadius GetWindowCornerRadius()
    {
        var app = Application.Current;
        if (app != null &&
            app.TryGetResource("WindowCornerRadius", out var res) &&
            res is CornerRadius cr)
        {
            return cr;
        }

        return new CornerRadius(0);
    }

    private void ApplyCornerClip()
    {
        if (_clipHost == null)
            return;

        var bounds = _clipHost.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        if (WindowState == WindowState.Maximized)
        {
            _clipHost.Clip = null;
            if (_mainBackground != null)
                _mainBackground.Clip = null;
            return;
        }

        var cornerRadius = GetWindowCornerRadius();

        if (cornerRadius.TopLeft == 0 &&
            cornerRadius.TopRight == 0 &&
            cornerRadius.BottomLeft == 0 &&
            cornerRadius.BottomRight == 0)
        {
            _clipHost.Clip = null;
            if (_mainBackground != null)
                _mainBackground.Clip = null;
            return;
        }

        _roundedClip = CreateRoundedGeometry(bounds, cornerRadius);
        _clipHost.Clip = _roundedClip;
        if (_mainBackground != null)
            _mainBackground.Clip = _roundedClip.Clone();
    }

    private static Geometry CreateRoundedGeometry(Rect bounds, CornerRadius radius)
    {
        double width = bounds.Width;
        double height = bounds.Height;

        double tl = Math.Min(radius.TopLeft, Math.Min(width / 2, height / 2));
        double tr = Math.Min(radius.TopRight, Math.Min(width / 2, height / 2));
        double br = Math.Min(radius.BottomRight, Math.Min(width / 2, height / 2));
        double bl = Math.Min(radius.BottomLeft, Math.Min(width / 2, height / 2));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(tl, 0), true);
            ctx.LineTo(new Point(width - tr, 0));
            if (tr > 0) ctx.ArcTo(new Point(width, tr), new Size(tr, tr), 0, false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(width, height - br));
            if (br > 0) ctx.ArcTo(new Point(width - br, height), new Size(br, br), 0, false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(bl, height));
            if (bl > 0) ctx.ArcTo(new Point(0, height - bl), new Size(bl, bl), 0, false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(0, tl));
            if (tl > 0) ctx.ArcTo(new Point(tl, 0), new Size(tl, tl), 0, false, SweepDirection.Clockwise);
            ctx.EndFigure(true);
        }

        return geometry;
    }

    private void NavigateToProcesses(object? sender, RoutedEventArgs e)
    {
        if (_mainContent != null)
            _mainContent.Content = new Views.ProcessesView();
    }

    private void NavigateToSettings(object? sender, RoutedEventArgs e)
    {
        if (_mainContent != null)
            _mainContent.Content = new Views.SettingsView();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        
        // Solo manejar clic izquierdo; el menú contextual lo maneja Avalonia automáticamente
        if (point.Properties.IsLeftButtonPressed)
        {
            // Doble-clic para maximizar/restaurar
            if (e.ClickCount == 2)
            {
                ToggleWindowState(sender, null);
                e.Handled = true;
                return;
            }

            // Arrastrar ventana
            BeginMoveDrag(e);
        }
    }

    private void OnContextMenuOpening(object? sender, CancelEventArgs e)
    {
        // Actualizar estados habilitados de los items del menú
        if (_ctxRestore != null)
            _ctxRestore.IsEnabled = WindowState != WindowState.Normal;
        if (_ctxMinimize != null)
            _ctxMinimize.IsEnabled = WindowState != WindowState.Minimized;
        if (_ctxMaximize != null)
            _ctxMaximize.IsEnabled = WindowState != WindowState.Maximized;
        if (_ctxClose != null)
            _ctxClose.IsEnabled = true;
    }
}
