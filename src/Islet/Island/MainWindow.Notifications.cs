using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Islet
{
    public partial class MainWindow
    {
        // A short "live activity" that drops out of the island
        sealed record NotifyItem(
            string App,
            string Sender,
            string Message,
            BitmapSource? Icon,
            Color Badge,
            int Glyph,
            Action OnClick,
            Geometry? Logo = null,
            string? DragPath = null);

        NotifyItem? currentNotify;
        readonly Queue<NotifyItem> notifyQueue = new();

        // ================= Notifications =================

        void EnqueueChat(ChatNotification n)
        {
            // During a focus session it waits, to arrive with the rest at the end
            if (HoldForFocus(n))
                return;

            var (badge, url, logo) = n.App switch
            {
                ChatApp.Discord => (Color.FromRgb(88, 101, 242),
                    n.FromBrowser ? "https://discord.com/app" : "discord://", "DiscordLogo"),
                ChatApp.WhatsApp => (Color.FromRgb(37, 211, 102),
                    n.FromBrowser ? "https://web.whatsapp.com" : "whatsapp://", "WhatsAppLogo"),
                ChatApp.Messenger => (Color.FromRgb(0, 132, 255), "https://www.messenger.com", "MessengerLogo"),
                _ => (Color.FromRgb(72, 72, 74), null, null)
            };

            Enqueue(new NotifyItem(
                n.AppName,
                n.Sender,
                Settings.NotifyShowMessage && n.Message.Length > 0 ? n.Message : n.App == ChatApp.Other ? "New notification" : "New message",
                n.Icon,
                badge,
                0xE8BD,
                () =>
                {
                    // Another app's notification opens Windows' notification centre, where it can be acted on
                    if (url != null)
                        OpenUrl(url);
                    else if (n.App == ChatApp.Other)
                        OpenUrl("ms-actioncenter:");
                },
                logo == null ? null : (Geometry)FindResource(logo)));
        }

        // ================= The yellow dot =================
        //
        // With the island's notifications off, a small yellow dot on its top-right corner says some
        // arrived; it goes once they're read or cleared in Windows, or on clicking it, which opens
        // Windows' notification centre. Unlike the mic and camera dot, it sits on the island itself.

        void SetUpQuietDot()
        {
            notifications.QuietChanged += count => Dispatcher.BeginInvoke(ShowQuietDot);
        }

        void ShowQuietDot()
        {
            int count = notifications.QuietCount;
            bool hiddenForApp = fullscreen.ProcessName != null && Settings.ShouldHideFor(fullscreen.ProcessName);
            bool show = !Settings.ShowNotifications && count > 0 && !hiddenForApp && islandOpacity.Value > 0.5;

            NotifyDot.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            NotifyDot.ToolTip = count == 1 ? "1 new notification" : $"{count} new notifications";
            PlaceQuietDot();
        }

        // Follows the island every frame, perched on its top-right corner
        void PlaceQuietDot()
        {
            if (NotifyDot.Visibility != Visibility.Visible)
                return;

            double w = Math.Max(1, width.Value);
            double scale = islandScale.Value * dragPop.Value;
            double visualLeft = islandLeft + islandOrigin * w * (1 - scale);
            double size = NotifyDot.Width;
            double corner = Math.Min(height.Value / 2, 19) * scale;

            // On the curve of the corner, a little outside it, like an app's unread badge
            NotifyDot.Margin = new Thickness(
                visualLeft + w * scale - corner * 0.3 - size / 2,
                IslandTopInWindow(height.Value * scale) + corner * 0.3 - size / 2, 0, 0);
        }

        void NotifyDot_Click(object sender, MouseButtonEventArgs e) => OpenUrl("ms-actioncenter:");

        void Enqueue(NotifyItem item)
        {
            // Don't pop up over a full-screen game or video
            if (fullscreen.ProcessName != null && Settings.ShouldHideFor(fullscreen.ProcessName))
                return;

            notifyQueue.Enqueue(item);

            if (currentNotify == null)
                ShowNextNotification();
        }

        void ShowNextNotification()
        {
            if (notifyQueue.Count == 0)
            {
                currentNotify = null;
                UpdateSurface();
                return;
            }

            bool replacing = currentNotify != null;
            var item = notifyQueue.Dequeue();
            currentNotify = item;

            NotifyApp.Text = item.App;
            NotifySender.Text = item.Sender;
            NotifyMessage.Text = item.Message;

            // The known apps always get their own logo; Windows often has none for them
            NotifyIcon.Source = item.Logo == null ? item.Icon : null;
            NotifyLogo.Data = item.Logo;
            NotifyLogo.Visibility = item.Logo == null ? Visibility.Collapsed : Visibility.Visible;
            NotifyBadgeGlyph.Visibility = item.Logo == null ? Visibility.Visible : Visibility.Collapsed;
            NotifyBadge.Background = new SolidColorBrush(item.Badge);
            NotifyBadgeGlyph.Text = ((char)item.Glyph).ToString();

            hoverTimer.Stop();
            notifyTimer.Stop();
            notifyTimer.Start();

            // A new message while one is showing gives the island a little nudge
            if (replacing)
            {
                width.Velocity += 260;
                height.Velocity += 90;
            }

            UpdateSurface();
            EnsureRendering();
        }

        void Notify_Click(object sender, MouseButtonEventArgs e)
        {
            var item = currentNotify;

            if (item == null)
                return;

            e.Handled = true;

            // Letting go after dragging a screenshot out isn't a click
            if (notifyDragged)
            {
                notifyDragged = false;
                return;
            }

            notifyTimer.Stop();
            ShowNextNotification();
            item.OnClick();
        }
    }
}
