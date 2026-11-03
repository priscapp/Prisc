using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Prisc.Config;
using Prisc.UI.Helpers;

namespace Prisc.UI.Config
{
    /// <summary>
    /// The Reset button in a settings group's header: puts every setting the group shows back to
    /// its default, after a "Confirm reset" prompt. Enabled only while at least one of them differs
    /// from its default, so the button doubles as "this group has been changed". Used by the
    /// generated pages (one per [Category] section) and by the hand-built ones, which register
    /// their rows explicitly.
    /// </summary>
    /// <remarks>
    /// <para>A row counts only while it is on the page: a setting hidden by [VisibleWhen] (or by
    /// platform) is neither reset nor able to enable the button, so the group never claims changes
    /// the user cannot see. Each entry may name the control whose visibility stands for its row.</para>
    /// <para>The settings objects outlive every settings window, so the button watches them only
    /// while it is attached to the visual tree, and re-reads on every attach.</para>
    /// </remarks>
    public sealed class SettingsGroupReset
    {
        private const double IconSize = 16;

        private readonly string _title;
        private readonly List<Entry> _entries = new();
        private readonly Dictionary<Type, object> _defaults = new();
        private readonly List<INotifyPropertyChanged> _watching = new();
        private readonly List<AvaloniaObject> _watchingControls = new();
        private Button _button;
        private bool _attached;

        public SettingsGroupReset(string title)
        {
            _title = title;
        }

        /// <summary>Whether any entry was registered (a group with nothing resettable gets no button).</summary>
        public bool IsEmpty => _entries.Count == 0;

        /// <summary>A plain settings property, compared with and reset to the value a new
        /// <paramref name="settings"/> of the same type holds (see <see cref="SettingsReset"/>).</summary>
        /// <param name="row">The control whose visibility stands for the row; null for always shown.</param>
        public void AddProperty(object settings, string propertyName, Control row = null)
        {
            var pd = TypeDescriptor.GetProperties(settings)[propertyName]
                ?? throw new ArgumentException($"{settings.GetType().Name} has no property {propertyName}.", nameof(propertyName));
            AddProperty(settings, pd, row);
        }

        /// <inheritdoc cref="AddProperty(object, string, Control)"/>
        public void AddProperty(object settings, PropertyDescriptor pd, Control row = null)
        {
            _entries.Add(new Entry
            {
                IsDefault = () => SettingsReset.IsDefault(settings, pd, DefaultsFor(settings)),
                Reset = () => SettingsReset.Reset(settings, pd, DefaultsFor(settings)),
                Row = row,
                // the value too when it is a settings object of its own (FpsPresets, TimeOption):
                // its members change without the parent raising anything
                Sources = () => pd.GetValue(settings) is INotifyPropertyChanged nested and not System.Collections.IEnumerable
                    ? new[] { (INotifyPropertyChanged)settings, nested }
                    : new[] { (INotifyPropertyChanged)settings },
            });
        }

        /// <summary>A row with a reset of its own (the tool defaults, the keystroke style).
        /// <paramref name="source"/> is watched for changes; <paramref name="signal"/> /
        /// <paramref name="signalProperty"/>, when given, is a control property that changes with
        /// <paramref name="isDefault"/>.</summary>
        public void AddCustom(Func<bool> isDefault, Action reset, Control row = null, INotifyPropertyChanged source = null,
            AvaloniaObject signal = null, AvaloniaProperty signalProperty = null)
        {
            _entries.Add(new Entry
            {
                IsDefault = isDefault,
                Reset = reset,
                Row = row,
                Sources = () => source == null ? Array.Empty<INotifyPropertyChanged>() : new[] { source },
                Signal = signal,
                SignalProperty = signalProperty,
            });
        }

        /// <summary>The group header: <paramref name="title"/> (default: the group's title) with
        /// the Reset button at the far right. Goes in a GroupBox's Header.</summary>
        public Control CreateHeader(string title = null)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(new TextBlock { Text = title ?? _title, VerticalAlignment = VerticalAlignment.Center });

            var button = CreateButton();
            Grid.SetColumn(button, 1);
            grid.Children.Add(button);
            return grid;
        }

        /// <summary>The icon button alone, for a header that is laid out by hand (Hotkeys).</summary>
        public Button CreateButton()
        {
            if (_button != null)
                throw new InvalidOperationException("A group has one Reset button.");

            var icon = new PathIcon { Width = IconSize, Height = IconSize };
            if (Application.Current?.TryFindResource("IconResetGroup", out var geometry) == true && geometry is Geometry g)
                icon.Data = g;

            _button = new Button
            {
                Content = icon,
                Padding = new Thickness(6, 4),
                VerticalAlignment = VerticalAlignment.Center,
                // the header text sets the header's height; the button may overhang it a little
                // rather than push the group's content down
                Margin = new Thickness(8, -6, 0, -6),
            };
            var app = Application.Current;
            if (app != null && app.TryGetResource("BorderlessButton", app.ActualThemeVariant, out var theme) && theme is ControlTheme borderless)
                _button.Theme = borderless;
            _button.Classes.Add("Tertiary");
            ToolTip.SetShowOnDisabled(_button, true);
            AutomationProperties.SetName(_button, $"Reset {_title} settings");

            _button.Click += OnClick;
            _button.AttachedToVisualTree += (_, _) => Attach();
            _button.DetachedFromVisualTree += (_, _) => Detach();
            Update();
            return _button;
        }

        private object DefaultsFor(object settings)
        {
            var type = settings.GetType();
            if (!_defaults.TryGetValue(type, out var defaults))
                _defaults[type] = defaults = SettingsReset.CreateDefaults(settings);
            return defaults;
        }

        private IEnumerable<Entry> ShownEntries() => _entries.Where(e => e.Row == null || e.Row.IsVisible);

        private async void OnClick(object sender, RoutedEventArgs e)
        {
            e.Handled = true; // not a click on the header (the Permissions header is a toggle)
            if (!ShownEntries().Any(entry => !entry.IsDefault()))
                return;

            if (!await NiceDialog.ShowDialogAsync(_button, NiceDialogIcon.Warning,
                    $"Put every setting under {_title} back to its default? This can't be undone.",
                    "Confirm reset", "Reset", "Cancel"))
                return;

            // the rows to reset are the ones shown when the user said yes: resetting one setting
            // can show or hide others ([VisibleWhen] on a toggle in the same group), and those were
            // not part of what was confirmed
            foreach (var entry in ShownEntries().ToList())
                entry.Reset();

            Update();
        }

        private void Attach()
        {
            _attached = true;
            Resubscribe();
            Update();
        }

        private void Detach()
        {
            _attached = false;
            Unsubscribe();
        }

        private void Resubscribe()
        {
            Unsubscribe();
            if (!_attached)
                return;

            foreach (var source in _entries.SelectMany(e => e.Sources()).Distinct())
            {
                source.PropertyChanged += OnSourceChanged;
                _watching.Add(source);
            }

            foreach (var control in _entries.SelectMany(e => new[] { e.Row, e.Signal }).OfType<AvaloniaObject>().Distinct())
            {
                control.PropertyChanged += OnControlPropertyChanged;
                _watchingControls.Add(control);
            }
        }

        private void Unsubscribe()
        {
            foreach (var source in _watching)
                source.PropertyChanged -= OnSourceChanged;
            _watching.Clear();
            foreach (var control in _watchingControls)
                control.PropertyChanged -= OnControlPropertyChanged;
            _watchingControls.Clear();
        }

        private void OnSourceChanged(object sender, PropertyChangedEventArgs e)
        {
            // a nested settings object may have been swapped for a new one; follow it
            Resubscribe();
            Update();
        }

        private void OnControlPropertyChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Visual.IsVisibleProperty
                || _entries.Any(entry => entry.Signal == sender && entry.SignalProperty == e.Property))
                Update();
        }

        private void Update()
        {
            if (_button == null)
                return;

            var changed = ShownEntries().Any(e => !e.IsDefault());
            _button.IsEnabled = changed;
            ToolTip.SetTip(_button, changed
                ? $"Reset {_title} to the default settings"
                : $"{_title} is already using the default settings");
        }

        private sealed class Entry
        {
            public Func<bool> IsDefault;
            public Action Reset;
            public Control Row;
            public Func<IEnumerable<INotifyPropertyChanged>> Sources;
            public AvaloniaObject Signal;
            public AvaloniaProperty SignalProperty;
        }
    }
}
