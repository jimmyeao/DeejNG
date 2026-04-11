using DeejNG.Models;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeejNG.Views
{
    /// <summary>
    /// Always-on-top floating window driven entirely by MainWindow's picker state machine.
    /// Never has logic of its own — MainWindow tells it what to show.
    /// </summary>
    public partial class ChannelPickerOverlay : Window
    {
        // Theme brushes resolved at runtime so the overlay matches the active theme
        private static readonly SolidColorBrush FallbackBg        = new(Color.FromRgb(0x1E, 0x1E, 0x2E));
        private static readonly SolidColorBrush FallbackSurface    = new(Color.FromRgb(0x36, 0x36, 0x50));
        private static readonly SolidColorBrush FallbackPrimary    = new(Color.FromRgb(0x3B, 0x82, 0xF6));
        private static readonly SolidColorBrush FallbackAccent     = new(Color.FromRgb(0x10, 0xB9, 0x81));
        private static readonly SolidColorBrush FallbackText       = new(Color.FromRgb(0xE5, 0xE7, 0xEB));
        private static readonly SolidColorBrush FallbackTextDim    = new(Color.FromRgb(0x9C, 0xA3, 0xAF));

        public ChannelPickerOverlay()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Fully refreshes the overlay to match the given picker state.
        /// Call this on open, on scroll, and on category cycle.
        /// </summary>
        public void Refresh(ChannelPickerState state, string channelName)
        {
            ChannelNameText.Text = channelName;
            UpdateCategoryTabs(state.Category);
            RebuildItemList(state.Items, state.SelectedIndex);
            ScrollToSelected(state.SelectedIndex, state.Items.Count);
        }

        // ── Private helpers ─────────────────────────────────────────────────────

        private Brush GetBrush(string key, Brush fallback)
        {
            var res = Application.Current.TryFindResource(key);
            return res is Brush b ? b : fallback;
        }

        private void UpdateCategoryTabs(PickerCategory active)
        {
            var activeBg   = GetBrush("PrimaryBrush",   FallbackPrimary);
            var activeText = GetBrush("BackgroundBrush", FallbackBg);
            var inactiveBg = Brushes.Transparent;
            var inactiveText = GetBrush("TextSecondaryBrush", FallbackTextDim);

            SetTab(AppsCategoryBorder,   AppsCategoryText,   active == PickerCategory.Apps,    activeBg, activeText, inactiveBg, inactiveText);
            SetTab(InputsCategoryBorder, InputsCategoryText, active == PickerCategory.Inputs,  activeBg, activeText, inactiveBg, inactiveText);
            SetTab(OutputsCategoryBorder,OutputsCategoryText,active == PickerCategory.Outputs, activeBg, activeText, inactiveBg, inactiveText);
        }

        private static void SetTab(Border border, TextBlock text, bool isActive,
            Brush activeBg, Brush activeText, Brush inactiveBg, Brush inactiveText)
        {
            border.Background = isActive ? activeBg : inactiveBg;
            text.Foreground   = isActive ? activeText : inactiveText;
        }

        private void RebuildItemList(List<AudioTarget> items, int selectedIndex)
        {
            ItemsPanel.Children.Clear();

            var surfaceHighlight = GetBrush("SurfaceHighlightBrush", FallbackSurface);
            var primaryBrush     = GetBrush("PrimaryBrush",           FallbackPrimary);
            var accentBrush      = GetBrush("AccentBrush",            FallbackAccent);
            var textBrush        = GetBrush("TextPrimaryBrush",       FallbackText);
            var dimBrush         = GetBrush("TextSecondaryBrush",     FallbackTextDim);

            for (int i = 0; i < items.Count; i++)
            {
                bool isSelected = i == selectedIndex;
                var target = items[i];

                // Type badge prefix
                string badge = target.IsInputDevice  ? "[IN] " :
                               target.IsOutputDevice ? "[OUT] " : "";

                // Display name (strip .exe for clarity)
                string rawName = target.Name;
                string displayName = rawName.EndsWith(".exe", System.StringComparison.OrdinalIgnoreCase)
                    ? rawName[..^4] : rawName;

                var rowGrid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                // Selection arrow
                var arrow = new TextBlock
                {
                    Text = "▶",
                    FontSize = 10,
                    Foreground = accentBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = isSelected ? Visibility.Visible : Visibility.Hidden
                };
                Grid.SetColumn(arrow, 0);

                // Badge
                var badgeText = new TextBlock
                {
                    Text = badge,
                    FontSize = 11,
                    Foreground = isSelected ? accentBrush : dimBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontStyle = FontStyles.Italic
                };
                Grid.SetColumn(badgeText, 1);

                // Name
                var nameText = new TextBlock
                {
                    Text = displayName,
                    FontSize = 13,
                    Foreground = isSelected ? primaryBrush : textBrush,
                    FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(nameText, 2);

                rowGrid.Children.Add(arrow);
                rowGrid.Children.Add(badgeText);
                rowGrid.Children.Add(nameText);

                var row = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 5, 8, 5),
                    Background = isSelected ? surfaceHighlight : Brushes.Transparent,
                    Child = rowGrid
                };

                ItemsPanel.Children.Add(row);
            }
        }

        private void ScrollToSelected(int selectedIndex, int totalCount)
        {
            if (totalCount == 0) return;

            // Dispatch to let layout complete before scrolling
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                if (selectedIndex < ItemsPanel.Children.Count)
                {
                    var child = ItemsPanel.Children[selectedIndex] as FrameworkElement;
                    child?.BringIntoView();
                }
            });
        }
    }
}
