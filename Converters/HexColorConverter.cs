// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PigeonPost.Converters;

/// <summary>
/// Resolves a log-level brush for use as a XAML <c>Foreground</c>.
///
/// <para>
/// <see cref="Models.LogEntry.LevelColor"/> returns a ThemeDictionary resource key
/// (e.g. <c>"LogFileBrush"</c>).  This converter looks the key up in
/// <c>Application.Current.Resources</c>, which automatically returns the Light or Dark
/// variant defined in <c>App.xaml</c> for the active Windows theme.
/// </para>
/// <para>
/// For backward compatibility a CSS hex string (e.g. <c>"#0969da"</c>) is also accepted
/// and parsed directly into a <see cref="SolidColorBrush"/>.
/// </para>
/// </summary>
public sealed class HexColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string key)
        {
            // Primary path: resolve a ThemeDictionary resource key so the brush
            // automatically reflects the active Light / Dark GitHub Primer palette.
            if (Application.Current.Resources.TryGetValue(key, out var res) &&
                res is SolidColorBrush themeBrush)
                return themeBrush;

            // Fallback: parse a raw CSS hex string (#rrggbb) for legacy callers.
            if (key.StartsWith('#') && key.Length == 7)
            {
                try
                {
                    var r = System.Convert.ToByte(key[1..3], 16);
                    var g = System.Convert.ToByte(key[3..5], 16);
                    var b = System.Convert.ToByte(key[5..7], 16);
                    return new SolidColorBrush(Color.FromArgb(255, r, g, b));
                }
                catch { /* fall through to default */ }
            }
        }

        // Fallback: neutral grey so the label is still readable if resolution fails.
        return new SolidColorBrush(Color.FromArgb(255, 87, 96, 106));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
