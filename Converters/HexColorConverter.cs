using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PigeonPost.Converters;

/// <summary>
/// Converts a CSS-style hex colour string (e.g. <c>"#0969da"</c> or <c>"#cf222e"</c>)
/// to a <see cref="SolidColorBrush"/> suitable for use as a XAML <c>Foreground</c>.
///
/// <para>
/// This converter exists because <see cref="Models.LogEntry.LevelColor"/> returns a
/// plain <see langword="string"/> rather than a <see cref="Brush"/>.  Returning a
/// <see cref="Brush"/> from inside a compiled <c>{x:Bind}</c> DataTemplate binding
/// requires the property getter to run on the UI thread AND to avoid accessing
/// <c>Application.Current.Resources</c> (which does not search ThemeDictionaries from
/// C# code).  A plain string sidesteps both issues entirely; the conversion to a brush
/// happens here, safely inside the XAML binding pipeline on the UI thread.
/// </para>
/// </summary>
public sealed class HexColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string hex && hex.StartsWith('#') && hex.Length == 7)
        {
            try
            {
                var r = System.Convert.ToByte(hex[1..3], 16);
                var g = System.Convert.ToByte(hex[3..5], 16);
                var b = System.Convert.ToByte(hex[5..7], 16);
                return new SolidColorBrush(Color.FromArgb(255, r, g, b));
            }
            catch { /* fall through to default */ }
        }

        // Fallback: neutral grey so the label is still readable if parsing fails.
        return new SolidColorBrush(Color.FromArgb(255, 87, 96, 106));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
