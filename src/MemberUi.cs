using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace DrangedAddons;

/// <summary>How a member looks in any list (friends, co-op lobbies, invites): their picture or initials, and a status dot.</summary>
public static class MemberUi
{
    /// <param name="state">The status to show ("online", "away", "ingame", "offline"); null for no dot.</param>
    public static FrameworkElement Avatar(FrameworkElement owner, CoopPlayer player, string? state, double size = 28)
    {
        var grid = new Grid { Width = size, Height = size, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(new Ellipse { Fill = Res(owner, "Panel2Brush"), Stroke = Res(owner, "LineBrush"), StrokeThickness = 1 });
        if (Avatars.Get(player.Id, player.Avatar) is { } picture)
            grid.Children.Add(new Ellipse { Fill = new ImageBrush(picture) { Stretch = Stretch.UniformToFill } });
        else
            grid.Children.Add(new TextBlock
            {
                Text = Account.Initials(player.Name),
                FontSize = size * 0.4,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res(owner, "AccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        if (state is not null)
            grid.Children.Add(new Ellipse
            {
                Width = size * 0.36,
                Height = size * 0.36,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -2, -2),
                Fill = Account.StateBrush(state),
                Stroke = Res(owner, "BgBrush"),
                StrokeThickness = 2,
            });
        return grid;
    }

    static Brush Res(FrameworkElement owner, string key) => (Brush)owner.FindResource(key);
}
