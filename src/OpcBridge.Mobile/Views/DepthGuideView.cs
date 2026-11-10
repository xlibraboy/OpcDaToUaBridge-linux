using Microsoft.Maui.Graphics;

namespace OpcBridge.Mobile.Views;

/// <summary>
/// The nesting rails beside an element row: one 1px vertical guide per enclosing level, on the
/// same step the row indents by. A lightweight grid of BoxViews rather than a GraphicsView —
/// the Elements list creates one of these per row, so the draws must stay cheap while scrolling.
/// The rails are grown once per view and shown or hidden on rebind; rebuilding the view tree on
/// every recycled row was what made a fling stutter.
/// </summary>
public sealed class DepthGuideView : Grid
{
    /// <summary>The indent step a row and its guides share (matches IndentWidth's 14 px per level).</summary>
    public const double Step = 14;

    private readonly List<BoxView> rails_ = new();

    public static readonly BindableProperty DepthProperty = BindableProperty.Create(
        nameof(Depth),
        typeof(int),
        typeof(DepthGuideView),
        0,
        propertyChanged: static (bindable, _, _) => ((DepthGuideView)bindable).ApplyDepth());

    public static readonly BindableProperty GuideColorProperty = BindableProperty.Create(
        nameof(GuideColor),
        typeof(Color),
        typeof(DepthGuideView),
        Colors.Transparent,
        propertyChanged: static (bindable, _, _) => ((DepthGuideView)bindable).Recolor());

    /// <summary>How many levels enclose the row; 0 draws nothing.</summary>
    public int Depth
    {
        get => (int)GetValue(DepthProperty);
        set => SetValue(DepthProperty, value);
    }

    public Color GuideColor
    {
        get => (Color)GetValue(GuideColorProperty);
        set => SetValue(GuideColorProperty, value);
    }

    private void ApplyDepth()
    {
        int depth = Math.Max(0, Depth);
        while (rails_.Count < depth)
        {
            BoxView rail = new()
            {
                Color = GuideColor,
                WidthRequest = 1,
                HorizontalOptions = LayoutOptions.Start,
                VerticalOptions = LayoutOptions.Fill,
                Margin = new Thickness(rails_.Count * Step, 0, 0, 0)
            };
            rails_.Add(rail);
            Children.Add(rail);
        }

        for (int level = 0; level < rails_.Count; level++)
        {
            rails_[level].IsVisible = level < depth;
        }
    }

    private void Recolor()
    {
        foreach (BoxView rail in rails_)
        {
            rail.Color = GuideColor;
        }
    }
}
