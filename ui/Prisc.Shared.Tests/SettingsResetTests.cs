using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Media;
using Prisc.Config;
using Xunit;

namespace Prisc.Shared.Tests
{
    /// <summary>The rules behind a settings group's header Reset (SettingsGroupReset in Prisc.Ui).</summary>
    public class SettingsResetTests
    {
        private static PropertyDescriptor Pd(object obj, string name) => TypeDescriptor.GetProperties(obj)[name];

        /// <summary>Every category a generated settings page shows: a fresh object must read as
        /// default on every row, or its group's Reset would be enabled on a clean install.</summary>
        public static IEnumerable<object[]> Categories() => new object[][]
        {
            new object[] { new SettingsCapture() },
            new object[] { new SettingsRecording() },
            new object[] { new SettingsShareRegion() },
            new object[] { new SettingsDrawOnScreen() },
            new object[] { new SettingsEditor() },
            new object[] { new SettingsGeneral() },
            new object[] { new SettingsUpload() },
        };

        [Theory]
        [MemberData(nameof(Categories))]
        public void FreshCategoryIsDefaultOnEveryProperty(object settings)
        {
            var defaults = SettingsReset.CreateDefaults(settings);
            Assert.NotSame(settings, defaults);

            var changed = TypeDescriptor.GetProperties(settings).Cast<PropertyDescriptor>()
                .Where(pd => !pd.IsReadOnly && !SettingsReset.IsDefault(settings, pd, defaults))
                .Select(pd => pd.Name)
                .ToList();
            Assert.Empty(changed);
        }

        [Fact]
        public void ResetRestoresPlainValues()
        {
            var settings = new SettingsRecording { Fps = 12, Quality = VideoQuality.Low, OutputDirectory = @"C:\elsewhere" };
            var defaults = SettingsReset.CreateDefaults(settings);

            foreach (var name in new[] { nameof(SettingsRecording.Fps), nameof(SettingsRecording.Quality), nameof(SettingsRecording.OutputDirectory) })
            {
                Assert.False(SettingsReset.IsDefault(settings, Pd(settings, name), defaults));
                SettingsReset.Reset(settings, Pd(settings, name), defaults);
                Assert.True(SettingsReset.IsDefault(settings, Pd(settings, name), defaults));
            }

            Assert.Equal(new SettingsRecording().Fps, settings.Fps);
            Assert.Equal(new SettingsRecording().OutputDirectory, settings.OutputDirectory);
        }

        [Fact]
        public void NestedSettingsObjectsCompareByMembersAndResetInPlace()
        {
            var settings = new SettingsRecording();
            var presets = settings.FpsPresets;
            var defaults = SettingsReset.CreateDefaults(settings);
            var pd = Pd(settings, nameof(SettingsRecording.FpsPresets));

            // a different instance with the same members is still the default
            Assert.True(SettingsReset.IsDefault(settings, pd, defaults));

            presets.Second = presets.Second + 7;
            Assert.False(SettingsReset.IsDefault(settings, pd, defaults));

            SettingsReset.Reset(settings, pd, defaults);
            Assert.Same(presets, settings.FpsPresets); // the page's editors are bound to this instance
            Assert.True(SettingsReset.IsDefault(settings, pd, defaults));
        }

        [Fact]
        public void TimeOptionResetsInPlace()
        {
            var settings = new SettingsEditor();
            var option = settings.DeleteSessionsAfter;
            option.Number = 2;
            option.Unit = TimeOptionUnit.Hours;
            var defaults = SettingsReset.CreateDefaults(settings);
            var pd = Pd(settings, nameof(SettingsEditor.DeleteSessionsAfter));

            SettingsReset.Reset(settings, pd, defaults);

            Assert.Same(option, settings.DeleteSessionsAfter);
            Assert.Equal(new SettingsEditor().DeleteSessionsAfter.Number, option.Number);
            Assert.Equal(new SettingsEditor().DeleteSessionsAfter.Unit, option.Unit);
        }

        [Fact]
        public void ResetOfAnUntouchedValueRaisesNothing()
        {
            var settings = new SettingsEditor();
            var raised = new List<string>();
            settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            var defaults = SettingsReset.CreateDefaults(settings);

            SettingsReset.Reset(settings, Pd(settings, nameof(SettingsEditor.CanvasBackground)), defaults);

            Assert.Empty(raised);
        }

        [Fact]
        public void ValuesEqualHandlesCollections()
        {
            Assert.True(SettingsReset.ValuesEqual(new List<string> { "a", "b" }, new List<string> { "a", "b" }));
            Assert.False(SettingsReset.ValuesEqual(new List<string> { "a", "b" }, new List<string> { "b", "a" }));
            Assert.True(SettingsReset.ValuesEqual(new Dictionary<int, string> { [1] = "x" }, new Dictionary<int, string> { [1] = "x" }));
            Assert.False(SettingsReset.ValuesEqual(new Dictionary<int, string> { [1] = "x" }, new Dictionary<int, string> { [1] = "y" }));
            Assert.True(SettingsReset.ValuesEqual(Colors.Red, Color.FromRgb(255, 0, 0)));
            Assert.False(SettingsReset.ValuesEqual(null, ""));
        }
    }
}
