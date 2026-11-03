using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Prisc.Config;
using Prisc.UI.Controls;
using Prisc.UI.DrawOnScreen;

namespace Prisc.UI.Config
{
    /// <summary>
    /// The Draw on Screen page's keystroke style block: the same <see cref="KeystrokeStylePicker"/>
    /// the video editor's inspector shows (style tiles, theme tiles, custom editor), over
    /// <see cref="SettingsDrawOnScreen"/> through <see cref="DrawKeystrokeStyleHost"/>. The settings
    /// page builds it for the <see cref="SettingsDrawOnScreen.KeystrokeStyle"/> row, as a block under
    /// the row's caption (it is far too big for the editor column). Resetting it is the section
    /// header's job (<see cref="SettingsGroupReset"/> over <see cref="DrawKeystrokeStyle.Reset"/>).
    /// </summary>
    /// <remarks>
    /// The settings object outlives every settings window, so it is watched only while this block
    /// is on screen, and re-read on every visit in case something else changed it meanwhile.
    /// </remarks>
    public class DrawKeystrokeStylePanel : Panel
    {
        /// <summary>The block's width cap: the inspector-sized picker grown to a settings column,
        /// wide enough for the tiles to read and narrow enough that the 2x2 grid stays a grid.</summary>
        private const double MaxBlockWidth = 460;

        private readonly SettingsDrawOnScreen _settings;
        private readonly DrawKeystrokeStyleHost _host;
        private readonly KeystrokeStyleEditor _editor;

        public DrawKeystrokeStylePanel(SettingsDrawOnScreen settings)
        {
            _settings = settings;
            _host = new DrawKeystrokeStyleHost(settings);
            _editor = new KeystrokeStyleEditor(_host);

            HorizontalAlignment = HorizontalAlignment.Left;
            Children.Add(new KeystrokeStylePicker { IsModern = true, DataContext = _editor });
        }

        /// <summary>The editor the picker is bound to (for tests and callers that drive it).</summary>
        public KeystrokeStyleEditor Editor => _editor;

        /// <summary>As wide as the page allows up to <see cref="MaxBlockWidth"/>, left-aligned like
        /// the rows above it. The tiles would otherwise ask only for their content's width (a Left
        /// block shrinks to it), and a Stretch block held back by MaxWidth is centered.</summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            var width = double.IsInfinity(availableSize.Width) ? MaxBlockWidth : System.Math.Min(availableSize.Width, MaxBlockWidth);
            var height = 0d;
            foreach (var child in Children)
            {
                child.Measure(new Size(width, availableSize.Height));
                height = System.Math.Max(height, child.DesiredSize.Height);
            }

            return new Size(width, height);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _settings.PropertyChanged += OnSettingsPropertyChanged;
            _editor.Refresh();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _settings.PropertyChanged -= OnSettingsPropertyChanged;
            base.OnDetachedFromVisualTree(e);
        }

        private void OnSettingsPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not (null or ""
                or nameof(SettingsDrawOnScreen.KeystrokeStyle)
                or nameof(SettingsDrawOnScreen.KeystrokeTheme)
                or nameof(SettingsDrawOnScreen.KeystrokeCustom)))
                return;

            // the picker's own write re-reads once it is done; refreshing mid-write would show the
            // half-written state (the look already seeded, the style not yet custom)
            if (!_host.IsWriting)
                _editor.Refresh();
        }
    }
}
