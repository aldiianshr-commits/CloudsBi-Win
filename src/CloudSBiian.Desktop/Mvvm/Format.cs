using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CloudSBiian.Desktop.Mvvm;

public static class Format
{
    public static string Bytes(long? bytes)
    {
        if (bytes == null)
        {
            return "N/A";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double v = bytes.Value;
        var u = 0;
        while (Math.Abs(v) >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return u == 0 ? $"{v:0} {units[u]}" : $"{v:0.##} {units[u]}";
    }

    public static string Speed(double? bytesPerSecond) =>
        bytesPerSecond == null ? "N/A" : Bytes((long)bytesPerSecond.Value) + "/s";

    public static string Percent(double? ratio) => ratio == null ? "N/A" : $"{ratio.Value * 100:0.0}%";

    public static string Ms(double? ms) => ms == null ? "N/A" : ms < 1000 ? $"{ms:0} ms" : $"{ms / 1000:0.0} s";

    public static string Duration(double? seconds)
    {
        if (seconds == null)
        {
            return "N/A";
        }

        var t = TimeSpan.FromSeconds(seconds.Value);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{t.Minutes} min {t.Seconds} s" : $"{t.Seconds} s";
    }

    public static string Time(DateTimeOffset? t) => t == null ? "—" : t.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is bool v && v;
        if (value is int i)
        {
            b = i != 0;
        }
        else if (value is string s)
        {
            b = !string.IsNullOrEmpty(s);
        }
        else if (value != null && value is not bool)
        {
            b = true;
        }

        if (Invert)
        {
            b = !b;
        }

        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Maps a status keyword to a brush (ok / warn / error / neutral).</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value?.ToString() ?? string.Empty;
        var key = s switch
        {
            "Connected" or "Supported" or "Synced" or "Ok" or "Mounted" or "Running" => "OkBrush",
            "Connecting" or "Experimental" or "PendingUpload" or "Uploading" or "Warning" or "Info" or "Offline" => "WarnBrush",
            "Error" or "Blocked" or "AuthenticationExpired" or "AuthenticationFailed" or "RetryRequired" or "Conflict" => "ErrorBrush",
            _ => "MutedBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
