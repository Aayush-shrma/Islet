using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Islet
{
    public enum ChatApp
    {
        Discord,
        WhatsApp,
        Messenger,
        Browser,

        /// <summary>Any other app: Mail, Teams, Outlook, a game launcher, Windows itself.</summary>
        Other
    }

    public sealed record ChatNotification(
        ChatApp App,
        string AppName,
        string Sender,
        string Message,
        BitmapSource? Icon,
        bool FromBrowser)
    {
        /// <summary>Its place in Windows' list of notifications, to clear it from there later.</summary>
        public uint? WindowsId { get; init; }
    }

    /// <summary>
    /// Reads Windows toast notifications (with the user's permission): Discord, WhatsApp and
    /// Messenger messages, given their own look, and every other app's too. Those apps are recognised
    /// by name, which includes web apps installed from the browser ("Install
    /// app"). A plain browser tab's notification is recognised by the site
    /// address the browser adds as its last line; other sites are optional and
    /// labelled with the browser's name.
    /// </summary>
    public sealed class NotificationWatcher
    {
        readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(1200) };
        readonly HashSet<uint> seen = new();

        // Ones that arrived while the island's notifications were off, still in Windows' list
        readonly HashSet<uint> quiet = new();

        UserNotificationListener? listener;
        bool polling;

        public UserNotificationListenerAccessStatus Status { get; private set; } =
            UserNotificationListenerAccessStatus.Unspecified;

        public event Action<ChatNotification>? Received;

        /// <summary>How many notifications came while the island's were off and are still unread in Windows.</summary>
        public event Action<int>? QuietChanged;

        /// <summary>
        /// Windows' own announcement of a pop-up appearing is being heard (see WinEventWatcher). Then
        /// the notification list is looked at the moment one does, and only slowly otherwise. Until
        /// that has been seen to work, it's looked at quickly all the time, as a precaution.
        /// </summary>
        public bool EventsAvailable { get; set; }

        bool eventsProven;
        bool pollFromEvent;
        bool takenSinceEvent;

        /// <summary>True while notifications are being looked at.</summary>
        public bool Running => listener != null && timer.IsEnabled;

        /// <summary>A pop-up appeared, but no notification the island took over came with it.</summary>
        public event Action? PopupNotTaken;
        bool pollAgain;
        DateTime burstUntil = DateTime.MinValue;

        /// <summary>Looks at the notification list now, and keeps looking closely for a moment, as a pop-up appeared.</summary>
        public void PollNow(bool fromEvent)
        {
            if (listener == null || !timer.IsEnabled)
                return;

            if (fromEvent)
            {
                if (!pollFromEvent)
                    takenSinceEvent = false;

                pollFromEvent = true;
                burstUntil = DateTime.UtcNow.AddMilliseconds(1500);
                timer.Interval = TimeSpan.FromMilliseconds(40);
            }

            if (polling)
            {
                pollAgain = true;
                return;
            }

            _ = PollAsync();
        }

        /// <summary>
        /// True while the island is holding notifications back for Do Not Disturb: they're left in
        /// Windows' notification centre as well, so none is lost if Islet closes meanwhile.
        /// </summary>
        public bool KeepInWindows { get; set; }

        /// <summary>Takes a notification out of Windows' list, once the island has really shown it.</summary>
        public void Remove(uint id)
        {
            try
            {
                listener?.RemoveNotification(id);
            }
            catch (Exception ex)
            {
                Log.Info("Couldn't clear a notification from Windows: " + ex.Message);
            }
        }

        public int QuietCount => quiet.Count;

        static readonly string[] Browsers =
            { "chrome", "edge", "brave", "firefox", "opera", "vivaldi" };

        static readonly Regex ChatSite = new(
            @"^(https?://)?([\w-]+\.)*(?<site>discord|whatsapp|messenger)\.com(/\S*)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public NotificationWatcher()
        {
            timer.Tick += async (s, e) => await PollAsync();
        }

        public async Task<bool> StartAsync()
        {
            if (timer.IsEnabled)
                return true;

            try
            {
                listener = UserNotificationListener.Current;
                Status = await listener.RequestAccessAsync();
                Log.Info("Notification access: " + Status);

                if (Status != UserNotificationListenerAccessStatus.Allowed)
                    return false;

                // Don't replay what's already in the notification center
                foreach (var n in await listener.GetNotificationsAsync(NotificationKinds.Toast))
                    seen.Add(n.Id);

                timer.Start();
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Notification listener unavailable", ex);
                Status = UserNotificationListenerAccessStatus.Denied;
                return false;
            }
        }

        public void Stop() => timer.Stop();

        async Task PollAsync()
        {
            if (polling || listener == null)
                return;

            polling = true;

            try
            {
                var settings = AppSettings.Current;

                // While Windows' own pop-ups are being cleared they have to be caught at once: by the
                // announcement of the pop-up where that's heard and proven, else by looking often
                bool clearing = settings.ShowNotifications && settings.HideWindowsPopups;
                bool burst = DateTime.UtcNow < burstUntil;
                bool fast = clearing && !(EventsAvailable && eventsProven);

                timer.Interval = TimeSpan.FromMilliseconds(burst ? 40 : fast ? 100 : 1200);

                var current = await listener.GetNotificationsAsync(NotificationKinds.Toast);
                var ids = new HashSet<uint>();
                int quietBefore = quiet.Count;

                foreach (var n in current)
                {
                    ids.Add(n.Id);

                    if (seen.Contains(n.Id))
                        continue;

                    var parsed = await ParseAsync(n);

                    if (parsed == null)
                        continue;

                    // A notification found just after a pop-up announced itself: that announcement works
                    if (pollFromEvent && !eventsProven)
                    {
                        eventsProven = true;
                        Log.Info("Notification pop-ups are heard as they appear; the fast checking is no longer needed");
                    }

                    if (!settings.ShowNotifications)
                    {
                        quiet.Add(n.Id);
                        continue;
                    }

                    Received?.Invoke(parsed);
                    takenSinceEvent = true;

                    // The island has it now; taking it out of Windows closes Windows' pop-up too, unless
                    // it's only being held for Do Not Disturb (Windows shows no pop-up then anyway)
                    if (settings.HideWindowsPopups && !KeepInWindows)
                    {
                        try
                        {
                            listener.RemoveNotification(n.Id);
                        }
                        catch (Exception ex)
                        {
                            Log.Info("Couldn't clear a notification from Windows: " + ex.Message);
                        }
                    }
                }

                // Forget dismissed notifications so the set doesn't grow forever; the quiet ones
                // count only while they're still unread in Windows
                seen.Clear();
                seen.UnionWith(ids);
                quiet.IntersectWith(ids);

                if (settings.ShowNotifications)
                    quiet.Clear();

                if (quiet.Count != quietBefore)
                    QuietChanged?.Invoke(quiet.Count);
            }
            catch (Exception ex)
            {
                Log.Error("Reading notifications failed", ex);
            }
            finally
            {
                polling = false;

                // An announcement came in during the look: look again straight away
                if (pollAgain)
                {
                    pollAgain = false;
                    _ = PollAsync();
                }
                else if (pollFromEvent && DateTime.UtcNow >= burstUntil)
                {
                    pollFromEvent = false;

                    if (!takenSinceEvent)
                        PopupNotTaken?.Invoke();
                }
            }
        }

        static async Task<ChatNotification?> ParseAsync(UserNotification n)
        {
            string appName = n.AppInfo?.DisplayInfo?.DisplayName ?? "";
            var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);

            if (binding == null)
                return null;

            var texts = binding.GetTextElements().Select(t => t.Text?.Trim() ?? "")
                .Where(t => t.Length > 0).ToList();

            var (app, fromBrowser) = Classify(appName, texts);

            if (app == null || !IsEnabled(app.Value) || texts.Count == 0)
                return null;

            BitmapSource? icon = null;

            try
            {
                var logo = n.AppInfo?.DisplayInfo?.GetLogo(new Windows.Foundation.Size(64, 64));
                icon = await MediaController.LoadImageAsync(logo);
            }
            catch
            {
            }

            return new ChatNotification(
                app.Value,
                app.Value switch
                {
                    ChatApp.Discord => "Discord",
                    ChatApp.WhatsApp => "WhatsApp",
                    ChatApp.Messenger => "Messenger",
                    _ => appName.Length > 0 ? appName : "Windows"
                },
                texts[0],
                string.Join(" ", texts.Skip(1)),
                icon,
                fromBrowser) { WindowsId = n.Id };
        }

        /// <summary>
        /// Which chat app a toast belongs to. A browser tab's site address is
        /// removed from <paramref name="texts"/>, as it isn't part of the message.
        /// </summary>
        internal static (ChatApp? App, bool FromBrowser) Classify(string appName, List<string> texts)
        {
            string name = appName.ToLowerInvariant();
            bool fromBrowser = Browsers.Any(name.Contains);

            if (name.Contains("discord")) return (ChatApp.Discord, fromBrowser);
            if (name.Contains("whatsapp")) return (ChatApp.WhatsApp, fromBrowser);
            if (name.Contains("messenger")) return (ChatApp.Messenger, fromBrowser);

            // Any other app counts too, if that's wanted
            if (!fromBrowser)
                return (ChatApp.Other, false);

            var site = texts.Count > 1 ? ChatSite.Match(texts[^1]) : Match.Empty;

            if (!site.Success)
                return (ChatApp.Browser, true);

            texts.RemoveAt(texts.Count - 1);

            return (site.Groups["site"].Value.ToLowerInvariant() switch
            {
                "discord" => ChatApp.Discord,
                "whatsapp" => ChatApp.WhatsApp,
                _ => ChatApp.Messenger
            }, true);
        }

        static bool IsEnabled(ChatApp app)
        {
            var settings = AppSettings.Current;

            return app switch
            {
                ChatApp.Discord => settings.NotifyDiscord,
                ChatApp.WhatsApp => settings.NotifyWhatsApp,
                ChatApp.Messenger => settings.NotifyMessenger,
                ChatApp.Other => settings.NotifyOtherApps,
                // The browser's own, and websites': part of "every other app"
                _ => settings.NotifyOtherApps
            };
        }
    }
}
