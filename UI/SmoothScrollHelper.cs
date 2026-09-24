using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CraftStats;

/// <summary>Adds eased inertial wheel scrolling to any control containing a ScrollViewer.</summary>
public static class SmoothScrollHelper
{
    private static readonly DependencyProperty AnimatedVerticalOffsetProperty =
        DependencyProperty.RegisterAttached(
            "AnimatedVerticalOffset",
            typeof(double),
            typeof(SmoothScrollHelper),
            new PropertyMetadata(0.0, OnAnimatedVerticalOffsetChanged));

    public static void Enable(DependencyObject root)
    {
        var viewer = FindVisualChild<ScrollViewer>(root);
        if (viewer is null)
            return;

        if (root is not UIElement element)
            return;

        viewer.SetValue(AnimatedVerticalOffsetProperty, viewer.VerticalOffset);
        element.PreviewMouseWheel += (_, e) =>
        {
            if (viewer.ScrollableHeight <= 0)
                return;

            e.Handled = true;
            var current = viewer.VerticalOffset;
            var target = Math.Clamp(current - e.Delta / 120.0 * 3.0, 0, viewer.ScrollableHeight);
            var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            viewer.SetValue(AnimatedVerticalOffsetProperty, current);
            viewer.BeginAnimation(AnimatedVerticalOffsetProperty, animation, HandoffBehavior.Compose);
        };
    }

    private static void OnAnimatedVerticalOffsetChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is ScrollViewer viewer)
            viewer.ScrollToVerticalOffset((double)e.NewValue);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
                return found;
            var nested = FindVisualChild<T>(child);
            if (nested is not null)
                return nested;
        }
        return null;
    }
}
