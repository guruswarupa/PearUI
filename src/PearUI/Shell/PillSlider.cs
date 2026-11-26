using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace MacUI.Shell;

/// <summary>
/// macOS Control Center slider: one rounded pill, a white fill whose rounded end is the knob, and the
/// symbol (sun / speaker) inside the white part so it is always readable. Value is 0–100.
/// Drag or click anywhere on it; the mouse wheel nudges it.
/// </summary>
public sealed class PillSlider : Grid
{
    private const double H = 26;

    private readonly Border _track = new() { CornerRadius = new CornerRadius(H / 2) };
    private readonly Border _fill = new()
    {
        CornerRadius = new CornerRadius(H / 2), HorizontalAlignment = HorizontalAlignment.Left,
        Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA)),
    };
    private readonly Border _knob = new()
    {
        Width = H, Height = H, CornerRadius = new CornerRadius(H / 2), HorizontalAlignment = HorizontalAlignment.Left,
        Background = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x1C, 0, 0, 0)), BorderThickness = new Thickness(0.5),
        Effect = new DropShadowEffect { BlurRadius = 5, ShadowDepth = 0.5, Direction = 0, Opacity = 0.22 },
    };
    private readonly TextBlock _icon = new()
    {
        FontSize = 12.5, Width = H, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false,
        Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x73)),
    };

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(PillSlider),
        new FrameworkPropertyMetadata(0.0, (d, _) => ((PillSlider)d).UpdateLayoutParts()));

    /// <summary>0–100.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, Math.Clamp(value, 0, 100));
    }

    /// <summary>Raised only when the user moves the slider (not when Value is set in code).</summary>
    public event EventHandler<double> UserChanged;

    public string Glyph
    {
        get => _icon.Text;
        set => _icon.Text = value;
    }

    public PillSlider()
    {
        Height = H;
        Background = Brushes.Transparent; // whole area is clickable
        Cursor = Cursors.Arrow;
        _track.SetResourceReference(Border.BackgroundProperty, "Brush.SliderTrack");
        _icon.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
        Children.Add(_track);
        Children.Add(_fill);
        Children.Add(_knob);
        Children.Add(_icon);
        SizeChanged += (_, _) => UpdateLayoutParts();
        IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1 : 0.45;
    }

    private void UpdateLayoutParts()
    {
        double w = ActualWidth;
        if (w <= H) return;
        double x = (w - H) * Value / 100.0; // knob's left edge
        _fill.Width = x + H;
        _knob.Margin = new Thickness(x, 0, 0, 0);
    }

    private void SetFromPointer(double px)
    {
        double w = ActualWidth;
        if (w <= H) return;
        double v = Math.Clamp((px - H / 2) / (w - H) * 100.0, 0, 100);
        Value = v;
        UserChanged?.Invoke(this, v);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsEnabled) return;
        CaptureMouse();
        SetFromPointer(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured) SetFromPointer(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!IsEnabled) return;
        Value += e.Delta > 0 ? 5 : -5;
        UserChanged?.Invoke(this, Value);
        e.Handled = true;
    }
}
