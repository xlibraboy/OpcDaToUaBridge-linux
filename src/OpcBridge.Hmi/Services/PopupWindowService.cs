using Avalonia.Controls;
using Avalonia.Threading;
using OpcBridge.Hmi.Core;

namespace OpcBridge.Hmi.Services;

/// <summary>
/// Ensures at most one faceplate/trend window per tag binding key.
/// </summary>
public sealed class PopupWindowService
{
    private readonly Dictionary<TagBindingKey, Window> faceplates_ = new(TagBindingKeyComparer.Instance);
    private readonly Dictionary<TagBindingKey, Window> trends_ = new(TagBindingKeyComparer.Instance);
    private readonly object sync_ = new();

    public Window OpenOrFocus(
        TagBindingKey key,
        bool trend,
        Func<Window> factory,
        Window? owner = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return Dispatcher.UIThread.Invoke(() => OpenOrFocus(key, trend, factory, owner));
        }

        Dictionary<TagBindingKey, Window> map = trend ? trends_ : faceplates_;
        lock (sync_)
        {
            if (map.TryGetValue(key, out Window? existing) && existing is not null)
            {
                if (existing.IsVisible)
                {
                    existing.Activate();
                    existing.Focus();
                    return existing;
                }

                map.Remove(key);
            }

            Window window = factory();
            map[key] = window;
            window.Closed += (_, _) =>
            {
                lock (sync_)
                {
                    if (map.TryGetValue(key, out Window? current) && ReferenceEquals(current, window))
                    {
                        map.Remove(key);
                    }
                }
            };

            if (owner is not null)
            {
                window.Show(owner);
            }
            else
            {
                window.Show();
            }

            return window;
        }
    }

    public int OpenFaceplateCount
    {
        get { lock (sync_) { return faceplates_.Count; } }
    }

    public int OpenTrendCount
    {
        get { lock (sync_) { return trends_.Count; } }
    }
}
