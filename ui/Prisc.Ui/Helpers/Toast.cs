using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace Prisc.UI.Helpers
{
    /// <summary>Lightweight toast notifications backed by Avalonia's WindowNotificationManager,
    /// with one manager cached per host window (the Semi theme styles the NotificationCard). All
    /// members must be called from the UI thread — callers ensure this.</summary>
    public static class Toast
    {
        private static readonly ConditionalWeakTable<Window, Host> _hosts = new();

        /// <summary>A window's notification manager, and the toasts asked for before it could show any.</summary>
        private sealed class Host
        {
            public WindowNotificationManager Manager;
            public List<Notification> Pending = new(); // null once the manager has its template
        }

        public static void Show(Window window, string message, NotificationType type = NotificationType.Success)
        {
            if (window == null || String.IsNullOrEmpty(message))
                return;

            var host = _hosts.GetValue(window, CreateHost);
            var notification = new Notification(null, message, type, TimeSpan.FromSeconds(2.5));
            if (host.Pending != null)
                host.Pending.Add(notification);
            else
                host.Manager.Show(notification);
        }

        private static Host CreateHost(Window window)
        {
            var host = new Host();
            host.Manager = new WindowNotificationManager(window)
            {
                Position = NotificationPosition.BottomRight,
                MaxItems = 3,
            };

            // until its template is applied, a manager shows cards into a placeholder list that
            // OnApplyTemplate then swaps for the template's panel, so a toast shown on a window
            // that had none yet (a scrolling capture's outcome on the editor it just opened, the
            // first "Recording saved") was silently lost. hold those until the template is in;
            // TemplateApplied is raised before the manager's own OnApplyTemplate swaps the list,
            // hence the post.
            host.Manager.TemplateApplied += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                var pending = host.Pending;
                host.Pending = null;
                if (pending != null)
                    foreach (var notification in pending)
                        host.Manager.Show(notification);
            });

            // the manager is already in the window's adorner layer when the window has been shown,
            // so this applies the template now rather than at the next layout pass.
            host.Manager.ApplyTemplate();
            return host;
        }

        /// <summary>A reasonable window to host a toast or borrow a clipboard from when the caller
        /// has no window of its own: the active visible window, else any visible one.</summary>
        public static Window GetActiveOrMainWindow()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                return desktop.MainWindow
                       ?? desktop.Windows.FirstOrDefault(w => w.IsActive && w.IsVisible)
                       ?? desktop.Windows.FirstOrDefault(w => w.IsVisible);
            }

            return null;
        }

        /// <summary>Best-effort clipboard access for code with no window of its own: borrows the
        /// clipboard of any open window.</summary>
        public static IClipboard GetPrimaryClipboard() => GetActiveOrMainWindow()?.Clipboard;
    }
}
