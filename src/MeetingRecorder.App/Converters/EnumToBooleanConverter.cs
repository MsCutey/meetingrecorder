using System.Globalization;
using System.Windows.Data;

namespace MeetingRecorder.App.Converters;

public sealed class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value.ToString()?.Equals(parameter.ToString(), StringComparison.OrdinalIgnoreCase) == true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Enum.Parse(targetType, parameter.ToString()!, true) : System.Windows.Data.Binding.DoNothing;
}
