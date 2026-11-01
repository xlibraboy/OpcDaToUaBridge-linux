using Microsoft.Maui.Graphics;

namespace OpcBridge.Mobile.Views;

/// <summary>
/// The nesting rails beside an element row: one 1px vertical guide per enclosing level, on the
/// same step the row indents by. The web Result board draws the same rails on its .rl-kids.
/// </summary>
public sealed class DepthGuideView : GraphicsView
{
    /// <summary>The indent step a row and its guides share (matches IndentWidth's 14 px per level).</summary>
    public const double Step = 14;

    public static readonly BindableProperty DepthProperty = BindableProperty.Create(
        nameof(Depth),
        typeof(int),
        typeof(DepthGuideView),
        0,
        propertyChanged: static (bindable, _, _) => ((DepthGuideView)bindable).Invalidate());

    public static readonly BindableProperty GuideColorProperty = BindableProperty.Create(
        nameof(GuideColor),
        typeof(Color),
        typeof(DepthGuideView),
        Colors.Transparent,
        propertyChanged: static (bindable, _, _) => ((DepthGuideView)bindable).Invalidate());

    public DepthGuideView()
    {
        Drawable = new Rails(this);
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

    private sealed class Rails : IDrawable
    {
        private readonly DepthGuideView owner_;

        public Rails(DepthGuideView owner) => owner_ = owner;

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            int depth = Math.Max(0, owner_.Depth);
            if (depth == 0)
            {
                return;
            }

            canvas.StrokeColor = owner_.GuideColor;
            canvas.StrokeSize = 1;
            for (int level = 0; level < depth; level++)
            {
                float x = (float)(level * Step) + 0.5f;
                // One pixel past each end so consecutive rows' segments meet without a hairline gap.
                canvas.DrawLine(x, -1, x, dirtyRect.Height + 1);
            }
        }
    }
}
