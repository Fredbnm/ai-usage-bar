using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AIUsageBar;

/// <summary>A rounded bar that fills from the left; <see cref="Fraction"/> is 0–1.</summary>
internal sealed class UsageBar : Control
{
    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<UsageBar, double>(nameof(Fraction));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<UsageBar, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<UsageBar, IBrush?>(nameof(Track));

    static UsageBar() => AffectsRender<UsageBar>(FractionProperty, FillProperty, TrackProperty);

    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var radius = bounds.Height / 2;
        if (Track is { } track) context.DrawRectangle(track, null, new RoundedRect(bounds, radius));
        var fraction = Math.Clamp(double.IsFinite(Fraction) ? Fraction : 0, 0, 1);
        if (fraction <= 0 || Fill is not { } fill) return;
        // Never thinner than the bar is tall, so a tiny share still shows as a dot.
        var width = Math.Max(bounds.Width * fraction, bounds.Height);
        context.DrawRectangle(fill, null, new RoundedRect(new Rect(0, 0, Math.Min(width, bounds.Width), bounds.Height), radius));
    }
}
