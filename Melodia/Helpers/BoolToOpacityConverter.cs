using System;
using Microsoft.UI.Xaml.Data;

namespace Melodia.Helpers;

public sealed class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? 1.0 : 0.35;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
