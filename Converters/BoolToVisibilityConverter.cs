using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace sumi
{
    /// <summary>
    /// bool値をVisibilityに変換するコンバーターです。
    /// </summary>
    public partial class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool b)
            {
                return b ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
