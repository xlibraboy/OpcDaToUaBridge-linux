using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace OpcBridge.Hmi.Controls;

public sealed class SparklineControl : Control
{
    public static readonly StyledProperty<IEnumerable<double>?> PointsProperty =
        AvaloniaProperty.Register<SparklineControl, IEnumerable<double>?>(nameof(Points));

    public IEnumerable<double>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    static SparklineControl()
    {
        AffectsRender<SparklineControl>(PointsProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        IEnumerable<double>? raw = Points;
        if (raw is null)
        {
            return;
        }

        double[] pts = raw as double[] ?? raw.ToArray();
        if (pts.Length < 2 || Bounds.Width <= 1 || Bounds.Height <= 1)
        {
            return;
        }

        double min = pts.Min();
        double max = pts.Max();
        double range = max - min;
        if (range <= 0)
        {
            range = 1;
        }

        StreamGeometry geometry = new();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            for (int i = 0; i < pts.Length; i++)
            {
                double x = i * (Bounds.Width - 1) / (pts.Length - 1);
                double y = Bounds.Height - 1 - ((pts[i] - min) / range) * (Bounds.Height - 1);
                if (i == 0)
                {
                    ctx.BeginFigure(new Point(x, y), false);
                }
                else
                {
                    ctx.LineTo(new Point(x, y));
                }
            }
        }

        context.DrawGeometry(
            null,
            new Pen(Brushes.DeepSkyBlue, 1.5),
            geometry);
    }
}
