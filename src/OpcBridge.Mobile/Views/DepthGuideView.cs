using Microsoft.Maui.Graphics;

namespace OpcBridge.Mobile.Views;

/// <summary>
/// The nesting rails beside an element row: one 1px vertical guide per enclosing level, on the
/// same step the row indents by. A lightweight grid of BoxViews rather than a GraphicsView —
/// the Elements list creates one of these per row, so the draws must stay cheap while scrolling.
/// </summary>
public sealed class DepthGuideView : Grid
{
    /// <summary>The indent step a row and its guides share (matches IndentWidth's 14 px per level).</summary>
    public const double Step = 14;

    public static readonly BindableProperty DepthProperty = BindableProperty.Create(
        nameof(Depth),
        typeof(int),
        typeof(DepthGuideView),
        0,
        propertyChanged: static (bindable, _, _) => ((DepthGuideView)bindable).Rebuild());

    public static readonly BindableProperty GuideColorProperty = BindableProperty.Create(
        nameof(GuideColor),
        typeof(Color),
        typeof(DepthGuideView),
        Colors.Transparent,
        propertyChanged: static (bindable, _, _) => ((DepthGuideView)bindable).Recolor());

    public DepthGuideView()
    {
        Rebuild();
    }

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

    private void Rebuild()
    {
        ColumnDefinitions.Clear();
        Children.Clear();

        int depth = Math.Max(0, Depth);
        for (int level = 0; level < depth; level++)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Step - 1) });

            BoxView rail = new()
            {
                Color = GuideColor,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill
            };
            SetColumn((BindableObject)rail, level * 2);
            Children.Add(rail);
        }
    }

    private void Recolor()
    {
        foreach (IView child in Children)
        {
            if (child is BoxView rail)
            {
                rail.Color = GuideColor;
            }
        }
    }
}
