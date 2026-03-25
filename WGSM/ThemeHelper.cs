using System;
using System.Linq;
using System.Windows;
using ControlzEx.Theming;

namespace WindowsGSM
{
    /// <summary>
    /// Swaps the WGSM colour-palette dictionary and the MahApps base theme in one call.
    /// </summary>
    public static class ThemeHelper
    {
        private const string DarkPalette  = "Themes/WgsmDark.xaml";
        private const string LightPalette = "Themes/WgsmLight.xaml";

        public static void Apply(bool dark)
        {
            var app  = Application.Current;
            var dicts = app.Resources.MergedDictionaries;

            // Swap WGSM palette dict
            var existing = dicts.FirstOrDefault(d =>
                d.Source != null &&
                (d.Source.OriginalString.Contains("WgsmDark") ||
                 d.Source.OriginalString.Contains("WgsmLight")));

            if (existing != null)
                dicts.Remove(existing);

            dicts.Add(new ResourceDictionary
            {
                Source = new Uri(dark ? DarkPalette : LightPalette, UriKind.Relative)
            });

            // Swap MahApps base theme
            ThemeManager.Current.ChangeTheme(app, dark ? "Dark.Teal" : "Light.Teal");
        }
    }
}
