namespace Prisc.Interop
{
    /// <summary>The macOS application object (<c>prisc_app_set_activation_policy_macos</c>,
    /// <c>prisc_app_install_tracking_area_fix_macos</c>).</summary>
    public static unsafe class MacApp
    {
        /// <summary>
        /// Sets the activation policy and, when <paramref name="activateIfRegular"/> and the policy is
        /// <see cref="ActivationPolicy.Regular"/>, brings the app to the front in the same call (an
        /// Accessory to Regular flip alone leaves the new window behind the frontmost app). Setting
        /// the current policy again is a no-op. macOS main thread only: throws
        /// <see cref="InteropException"/> with <see cref="InteropStatus.State"/> elsewhere, and
        /// <see cref="InteropStatus.Unsupported"/> off macOS.
        /// </summary>
        public static void SetActivationPolicy(ActivationPolicy policy, bool activateIfRegular)
        {
            InteropLibrary.Require();
            Native.Check(NativeMethods.prisc_app_set_activation_policy_macos((int)policy, activateIfRegular ? (byte)1 : (byte)0),
                "prisc_app_set_activation_policy_macos");
        }

        /// <summary>
        /// Stops Avalonia's windows from resetting the cursor to the arrow when the pointer crosses
        /// a tracking area another view owns (macOS 26 leaves a stale window-edge area behind on
        /// every resize). Process-wide; call once at startup, after Avalonia's platform is up. Returns
        /// whether the fix is in place: false when there is nothing to patch. macOS main thread
        /// only: throws <see cref="InteropException"/> with <see cref="InteropStatus.State"/>
        /// elsewhere, and <see cref="InteropStatus.Unsupported"/> off macOS.
        /// </summary>
        public static bool InstallTrackingAreaFix()
        {
            InteropLibrary.Require();
            return Native.Check(NativeMethods.prisc_app_install_tracking_area_fix_macos(),
                "prisc_app_install_tracking_area_fix_macos") == InteropStatus.Ok;
        }
    }
}
