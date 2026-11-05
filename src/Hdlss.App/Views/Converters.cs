using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Hdlss.App.Views;

/// <summary>Visible when the value is non-null / non-empty / true; Parameter "invert" flips it.</summary>
public sealed class VisibleWhen : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i > 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter as string == "invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Resource key ("Accent", "Warn"…) → brush from the theme.</summary>
public sealed class ResourceBrush : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key ? Application.Current.TryFindResource(key) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
