using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using Prisc.Util;
using Microsoft.Win32;

namespace Prisc.UI
{
    /// <summary>
    /// Removes the classic (Windows 7 style) "Upload with Prisc" Explorer verbs that earlier builds
    /// registered: per-user keys under <c>HKCU\Software\Classes\*\shell</c> and
    /// <c>...\Directory\shell</c>. Prisc no longer writes these — the Explorer entry is only the
    /// Win11 sparse package (<see cref="SparsePackageManager"/>), which Explorer lists in the compact
    /// menu and under "Show more options" alike — so every one left behind is a duplicate line in
    /// the menu, or one launching an executable that no longer exists.
    ///
    /// Release, Debug and pre-rename builds each wrote their own key (<see cref="KnownVerbKeyNames"/>),
    /// and all of them go, whichever build runs this. Only keys whose command is an upload verb
    /// (<see cref="CliArgs.ParseUploadVerbExecutable"/>) are touched, so an unrelated key that
    /// happens to share a name survives. Idempotent and cheap (a handful of registry reads once
    /// nothing is left); safe to call on any OS, and never throws.
    /// </summary>
    internal static class ClassicContextMenuCleanup
    {
        private static readonly string[] ShellPaths = { @"Software\Classes\*\shell", @"Software\Classes\Directory\shell" };

        /// <summary>Every key name a Prisc or Clowd build has registered the verb under, release
        /// and debug.</summary>
        private static readonly string[] KnownVerbKeyNames =
        {
            Constants.PriscAppName,
            Constants.PriscAppName + ".DEBUG",
            Prisc.Legacy.LegacyClowd.RegistryName,
            Prisc.Legacy.LegacyClowd.RegistryNameDebug,
        };

        public static void Run()
        {
            if (OperatingSystem.IsWindows())
                RunWindows();
        }

        [SupportedOSPlatform("windows")]
        private static void RunWindows()
        {
            foreach (var shellPath in ShellPaths)
            {
                try
                {
                    using var shell = Registry.CurrentUser.OpenSubKey(shellPath, true);
                    if (shell == null)
                        continue;

                    foreach (var name in KnownVerbKeyNames)
                    {
                        string exe;
                        using (var command = shell.OpenSubKey(name + @"\command", false))
                            exe = CliArgs.ParseUploadVerbExecutable(command?.GetValue("") as string);

                        if (exe == null)
                            continue;

                        shell.DeleteSubKeyTree(name, false);
                        AppLog.Info("ClassicContextMenuCleanup: removed the " + name + " verb under " + shellPath + " (" + exe + ")");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("ClassicContextMenuCleanup: failed to remove verbs under " + shellPath + ": " + ex);
                    SentryConfig.CaptureHandled(ex, "shellreg.remove-classic-verbs");
                }
            }
        }
    }
}
