using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace ModernTaskManager.UI.Converters
{
    public class ResourceKeyConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string resourceKey && Application.Current!.TryGetResource(resourceKey, out var resource))
            {
                return resource;
            }
            return "Key Not Found";
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}