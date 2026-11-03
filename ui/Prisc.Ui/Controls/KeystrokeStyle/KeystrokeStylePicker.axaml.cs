using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Prisc.UI.Controls
{
    /// <summary>
    /// The keystroke overlay's style picker — style tiles, theme tiles and the custom editor — for
    /// any host: the video editor's inspector and Draw on Screen's settings page both show this one
    /// control.
    /// <para>
    /// <b>Host contract.</b> Set <see cref="StyledElement.DataContext"/> to a
    /// <see cref="KeystrokeStyleEditor"/> built over the host's <see cref="IKeystrokeStyleHost"/>,
    /// call its <see cref="KeystrokeStyleEditor.Refresh"/> whenever the host's values change from
    /// elsewhere, and set <see cref="IsModern"/> to the chrome the control sits in (true on a
    /// Semi-themed page). The control carries its own styles, tile theme and color popup, so it
    /// needs nothing else from its surroundings.
    /// </para>
    /// </summary>
    public partial class KeystrokeStylePicker : UserControl
    {
        /// <summary>Which chrome the control wears: false for the editor's original dense dark
        /// sidebar, true for the Semi-themed one (and any settings page).</summary>
        public static readonly StyledProperty<bool> IsModernProperty =
            AvaloniaProperty.Register<KeystrokeStylePicker, bool>(nameof(IsModern));

        public bool IsModern
        {
            get => GetValue(IsModernProperty);
            set => SetValue(IsModernProperty, value);
        }

        public KeystrokeStylePicker()
        {
            InitializeComponent();

            listStyle.ItemsSource = KeystrokeStyleEditor.StyleOptions;
            listTheme.ItemsSource = KeystrokeStyleEditor.ThemeOptions;

            // the color wells live in item templates, so their presses are caught on the way up
            AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble);
            miniColor.Canceled += (_, _) => colorPopup.IsOpen = false;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            // the mini picker's "pop out to the full dialog" needs an owner window
            miniColor.ParentWindow = TopLevel.GetTopLevel(this) as Window;
        }

        private void OnPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.Source is not Visual source)
                return;

            var well = source.GetSelfAndVisualAncestors()
                .OfType<Border>()
                .FirstOrDefault(b => b.Classes.Contains("ksWell"));

            if (well?.DataContext is not KeystrokeColorRow row)
                return;

            Color current = Color.FromUInt32(row.Argb);
            miniColor.Reset(current, c => row.Hex = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}");
            colorPopup.IsOpen = true;
            e.Handled = true;
        }
    }

    /// <summary>A packed ARGB color (<see cref="uint"/>) as a brush, for the color rows'
    /// swatches.</summary>
    public sealed class ArgbToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is uint argb ? new SolidColorBrush(Color.FromUInt32(argb)) : AvaloniaProperty.UnsetValue;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
