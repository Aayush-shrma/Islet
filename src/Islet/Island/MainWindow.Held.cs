using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Islet
{
    public partial class MainWindow
    {
        // ================= The orange dot =================
        //
        // While notifications are being held back, for Do Not Disturb or a focus session, a small
        // orange dot sits just outside the island, above the colour picker's dots (see
        // PlaceColorPill). Clicking it opens a list of what's waiting, each of which
        // can be opened or dismissed. When the quiet spell ends, what's left still arrives as one
        // card, as before.

        Popup? heldPopup;
        FrameworkElement? heldCard;
        readonly DispatcherTimer heldCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
        int heldAway;

        string HeldReason => windowsQuiet ? "Do Not Disturb" : "your focus session";

        void ShowHeldDot()
        {
            int count = heldForFocus.Count;
            bool hiddenForApp = fullscreen.ProcessName != null && Settings.ShouldHideFor(fullscreen.ProcessName);
            bool show = count > 0 && !hiddenForApp && islandOpacity.Value > 0.5;

            HeldPill.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            HeldPill.ToolTip = count == 1 ? $"1 notification held for {HeldReason}" : $"{count} notifications held for {HeldReason}";
            PlaceColorPill();

            if (heldPopup?.IsOpen == true)
            {
                if (count == 0 || !show)
                    CloseHeld();
                else
                    FillHeld();
            }
        }

        void HeldDot_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            if (heldPopup?.IsOpen == true)
                CloseHeld();
            else
                OpenHeld();
        }

        void OpenHeld()
        {
            if (heldForFocus.Count == 0)
                return;

            heldPopup ??= new Popup
            {
                AllowsTransparency = true,
                StaysOpen = true,
                PopupAnimation = PopupAnimation.Fade,
                Placement = PlacementMode.Custom,
                PlacementTarget = HeldPill,
                CustomPopupPlacementCallback = (popup, target, offset) => new[]
                {
                    // Under the dot with its right edge level with the dot's (above it at the bottom edge)
                    new CustomPopupPlacement(
                        new Point(target.Width - popup.Width + 6, atBottom ? -popup.Height - 6 : target.Height + 6),
                        PopupPrimaryAxis.None)
                }
            };

            FillHeld();
            heldPopup.IsOpen = true;

            // It goes by itself once the pointer has been away from it and the island a moment
            heldAway = 0;
            heldCloseTimer.Tick -= HeldCloseTick;
            heldCloseTimer.Tick += HeldCloseTick;
            heldCloseTimer.Start();
        }

        void CloseHeld()
        {
            heldCloseTimer.Stop();

            if (heldPopup != null)
                heldPopup.IsOpen = false;
        }

        void HeldCloseTick(object? sender, EventArgs e)
        {
            if (heldPopup?.IsOpen != true)
            {
                heldCloseTimer.Stop();
                return;
            }

            bool near = HeldPill.IsMouseOver || IslandContainer.IsMouseOver || heldCard?.IsMouseOver == true;
            heldAway = near ? 0 : heldAway + 1;

            if (heldAway >= 6)
                CloseHeld();
        }

        // The card: a heading, then each held notification
        void FillHeld()
        {
            var rows = new StackPanel();

            var heading = new Grid { Margin = new Thickness(8, 4, 4, 8) };
            heading.ColumnDefinitions.Add(new ColumnDefinition());
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new StackPanel();
            title.Children.Add(new TextBlock
            {
                Text = $"Held for {HeldReason}",
                Foreground = Brushes.White,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold
            });
            title.Children.Add(new TextBlock
            {
                Text = windowsQuiet ? "Also in Windows' notification centre" : "They arrive together when it's over",
                Foreground = new SolidColorBrush(Color.FromArgb(153, 255, 255, 255)),
                FontSize = 11
            });
            heading.Children.Add(title);

            var clear = HeldButton("Clear all", () =>
            {
                ClearHeld(heldForFocus.ToList());
            });
            Grid.SetColumn(clear, 1);
            heading.Children.Add(clear);
            rows.Children.Add(heading);

            foreach (var n in heldForFocus.ToList())
                rows.Children.Add(HeldRow(n));

            var scroll = new ScrollViewer
            {
                Content = rows,
                MaxHeight = 340,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            heldCard = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x1C, 0x1C, 0x1E)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(8),
                Margin = new Thickness(10),
                Width = 340,
                Child = scroll,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.45, Color = Colors.Black }
            };

            heldPopup!.Child = heldCard;
        }

        FrameworkElement HeldRow(ChatNotification n)
        {
            var item = BuildItem(n);

            var grid = new Grid { Margin = new Thickness(0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // The app's own icon, or its colour with a speech bubble
            FrameworkElement icon;

            if (n.Icon != null)
            {
                icon = new Border
                {
                    Width = 30,
                    Height = 30,
                    CornerRadius = new CornerRadius(8),
                    Background = new ImageBrush(n.Icon) { Stretch = Stretch.UniformToFill }
                };
            }
            else
            {
                icon = new Border
                {
                    Width = 30,
                    Height = 30,
                    CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(item.Badge),
                    Child = new TextBlock
                    {
                        Text = char.ConvertFromUtf32(item.Glyph),
                        FontFamily = (FontFamily)FindResource("IconFont"),
                        Foreground = Brushes.White,
                        FontSize = 14,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
            }

            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 2, 10, 0);
            grid.Children.Add(icon);

            var words = new StackPanel();
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(new TextBlock { Text = item.App, Foreground = Brushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });

            if (item.Sender.Length > 0)
                line.Children.Add(new TextBlock
                {
                    Text = " · " + item.Sender,
                    Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 150,
                    VerticalAlignment = VerticalAlignment.Center
                });

            words.Children.Add(line);
            words.Children.Add(new TextBlock
            {
                Text = item.Message,
                Foreground = new SolidColorBrush(Color.FromArgb(153, 255, 255, 255)),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 32
            });
            Grid.SetColumn(words, 1);
            grid.Children.Add(words);

            var dismiss = new Button
            {
                Content = "\uE711",
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 10,
                Width = 26,
                Height = 26,
                Cursor = Cursors.Hand,
                ToolTip = "Dismiss",
                VerticalAlignment = VerticalAlignment.Top,
                Template = HeldButtonTemplate(circle: true),
                Foreground = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255))
            };
            dismiss.Click += (s, e) => ClearHeld(new[] { n });
            Grid.SetColumn(dismiss, 2);
            grid.Children.Add(dismiss);

            // The whole row opens it, as clicking it on the island would
            var background = new SolidColorBrush(Colors.Transparent);
            var row = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 7, 6, 7), Background = background, Child = grid, Cursor = Cursors.Hand };
            row.MouseEnter += (s, e) => background.Color = Color.FromArgb(0x24, 255, 255, 255);
            row.MouseLeave += (s, e) => background.Color = Colors.Transparent;
            row.MouseLeftButtonUp += (s, e) =>
            {
                if (e.Handled)
                    return;

                item.OnClick();
                ClearHeld(new[] { n });
            };

            return row;
        }

        // Takes these out of what's held, and out of Windows' list too when that's being kept clear
        void ClearHeld(IEnumerable<ChatNotification> these)
        {
            foreach (var n in these)
            {
                heldForFocus.Remove(n);

                if (Settings.HideWindowsPopups && n.WindowsId is uint id)
                    notifications.Remove(id);
            }

            ShowHeldDot();
        }

        Button HeldButton(string text, Action click)
        {
            var button = new Button
            {
                Content = text,
                Cursor = Cursors.Hand,
                Foreground = Brushes.White,
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                Template = HeldButtonTemplate(circle: false),
                VerticalAlignment = VerticalAlignment.Top
            };
            button.Click += (s, e) => click();
            return button;
        }

        // A button that lightens under the pointer, round for the dismiss cross, a pill for the text
        static ControlTemplate HeldButtonTemplate(bool circle)
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border), "Bg");
            border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(circle ? (byte)0 : (byte)0x26, 255, 255, 255)));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(circle ? 13 : 11));
            border.SetValue(Border.PaddingProperty, circle ? new Thickness(0) : new Thickness(11, 4, 11, 5));

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);
            template.VisualTree = border;

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)), "Bg"));
            template.Triggers.Add(hover);
            return template;
        }
    }
}
