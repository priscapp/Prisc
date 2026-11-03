using System;
using System.Collections;
using System.ComponentModel;
using System.Linq;

namespace Prisc.Config
{
    /// <summary>
    /// What a settings group's Reset button compares against and writes back: the value a freshly
    /// constructed settings object of the same type holds. Platform-free so the rules are tested
    /// without a UI; the settings pages wire it to their groups.
    /// </summary>
    /// <remarks>
    /// Nested settings objects (<see cref="FpsPresets"/>, <see cref="TimeOption"/>) are reset in
    /// place, member by member, rather than replaced: their editors bind to the instance the page
    /// was built over, and a new instance would leave those editors showing the old values.
    /// </remarks>
    public static class SettingsReset
    {
        /// <summary>A defaults instance for <paramref name="settings"/>'s type. Every settings
        /// category has a public parameterless constructor (the settings binder needs one too), and
        /// constructing one has no side effects (see <see cref="SettingsRoot"/>).</summary>
        public static object CreateDefaults(object settings) => Activator.CreateInstance(settings.GetType());

        /// <summary>Whether <paramref name="pd"/> on <paramref name="settings"/> already holds the
        /// value it has on <paramref name="defaults"/>.</summary>
        public static bool IsDefault(object settings, PropertyDescriptor pd, object defaults) =>
            ValuesEqual(pd.GetValue(settings), pd.GetValue(defaults));

        /// <summary>Puts <paramref name="pd"/> on <paramref name="settings"/> back to its value on
        /// <paramref name="defaults"/>. Writes only what differs, so an untouched setting raises no
        /// change (and triggers none of the side effects its listeners would run).</summary>
        public static void Reset(object settings, PropertyDescriptor pd, object defaults)
        {
            var current = pd.GetValue(settings);
            var value = pd.GetValue(defaults);
            if (ValuesEqual(current, value))
                return;

            if (current is SimpleNotifyObject && value != null && current.GetType() == value.GetType())
            {
                foreach (PropertyDescriptor child in TypeDescriptor.GetProperties(current))
                {
                    if (!child.IsReadOnly)
                        Reset(current, child, value);
                }

                return;
            }

            pd.SetValue(settings, value);
        }

        /// <summary>Value equality as a settings page sees it: <see cref="object.Equals(object)"/>
        /// first, then element by element for collections and member by member for nested settings
        /// objects, neither of which overrides Equals.</summary>
        public static bool ValuesEqual(object a, object b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (a == null || b == null)
                return false;
            if (a.Equals(b))
                return true;
            if (a.GetType() != b.GetType())
                return false;

            if (a is IDictionary da && b is IDictionary db)
            {
                if (da.Count != db.Count)
                    return false;
                foreach (DictionaryEntry entry in da)
                {
                    if (!db.Contains(entry.Key) || !ValuesEqual(entry.Value, db[entry.Key]))
                        return false;
                }

                return true;
            }

            if (a is IEnumerable ea && b is IEnumerable eb && a is not string)
            {
                var la = ea.Cast<object>().ToList();
                var lb = eb.Cast<object>().ToList();
                return la.Count == lb.Count && la.Zip(lb).All(p => ValuesEqual(p.First, p.Second));
            }

            if (a is SimpleNotifyObject)
            {
                return TypeDescriptor.GetProperties(a).Cast<PropertyDescriptor>()
                    .Where(p => !p.IsReadOnly)
                    .All(p => ValuesEqual(p.GetValue(a), p.GetValue(b)));
            }

            return false;
        }
    }
}
