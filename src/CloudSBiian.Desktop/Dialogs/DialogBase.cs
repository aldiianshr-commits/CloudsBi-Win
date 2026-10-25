using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CloudSBiian.Desktop.Dialogs;

/// <summary>Code-built themed dialog window. Content is assembled by subclasses.</summary>
public class DialogBase : Window
{
    protected readonly StackPanel Body = new() { Margin = new Thickness(24, 20, 24, 8) };
    protected readonly StackPanel Buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 8, 16, 16) };

    public DialogBase(string title, double width = 520)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height * 0.92;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Owner = Application.Current?.MainWindow;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "ForegroundBrush");
        FontFamily = (FontFamily)Application.Current!.FindResource("UiFont");
        FontSize = 13;

        var root = new DockPanel();
        DockPanel.SetDock(Buttons, Dock.Bottom);
        root.Children.Add(Buttons);
        root.Children.Add(new ScrollViewer { Content = Body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    protected TextBlock AddHeading(string text)
    {
        var t = new TextBlock { Text = text };
        t.SetResourceReference(StyleProperty, "SectionTitle");
        Body.Children.Add(t);
        return t;
    }

    protected TextBlock AddText(string text, bool muted = true)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        if (muted)
        {
            t.SetResourceReference(StyleProperty, "Muted");
            t.Margin = new Thickness(0, 0, 0, 10);
        }

        Body.Children.Add(t);
        return t;
    }

    protected static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 4, 0, 4), FontWeight = FontWeights.SemiBold };

    protected TextBox AddTextBox(string label, string value = "", string? help = null, Panel? into = null)
    {
        var panel = into ?? Body;
        panel.Children.Add(Label(label));
        var box = new TextBox { Text = value };
        panel.Children.Add(box);
        if (help != null)
        {
            var h = new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -4, 0, 8) };
            h.SetResourceReference(StyleProperty, "Muted");
            panel.Children.Add(h);
        }

        return box;
    }

    protected PasswordBox AddPasswordBox(string label, string? help = null, Panel? into = null)
    {
        var panel = into ?? Body;
        panel.Children.Add(Label(label));
        var box = new PasswordBox();
        panel.Children.Add(box);
        if (help != null)
        {
            var h = new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -4, 0, 8) };
            h.SetResourceReference(StyleProperty, "Muted");
            panel.Children.Add(h);
        }

        return box;
    }

    protected Button AddButton(string text, RoutedEventHandler onClick, bool primary = false, bool isCancel = false)
    {
        var b = new Button { Content = text, MinWidth = 90, IsCancel = isCancel };
        if (primary)
        {
            b.SetResourceReference(StyleProperty, "PrimaryButton");
            b.IsDefault = true;
        }

        b.Click += onClick;
        Buttons.Children.Add(b);
        return b;
    }
}

public sealed class InputDialog : DialogBase
{
    private readonly TextBox _box;

    private InputDialog(string title, string prompt, string value)
        : base(title, 440)
    {
        _box = AddTextBox(prompt, value);
        AddButton("Cancel", (_, _) => DialogResult = false, isCancel: true);
        AddButton("OK", (_, _) => DialogResult = true, primary: true);
        Loaded += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
    }

    public static string? Ask(string title, string prompt, string value = "")
    {
        var d = new InputDialog(title, prompt, value);
        return d.ShowDialog() == true ? d._box.Text.Trim() : null;
    }
}

public sealed class TextReportDialog : DialogBase
{
    private TextReportDialog(string title, string text)
        : base(title, 640)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MinHeight = 200,
            MaxHeight = 520,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
        };
        Body.Children.Add(box);
        AddButton("Copy", (_, _) => Clipboard.SetText(text));
        AddButton("Close", (_, _) => Close(), primary: true, isCancel: true);
    }

    public static void Show(string title, string text) => new TextReportDialog(title, text).ShowDialog();
}

/// <summary>Lets the user pick items from a list (multi-select).</summary>
public sealed class PickListDialog : DialogBase
{
    private readonly ListBox _list;

    private PickListDialog(string title, string prompt, IReadOnlyList<string> items)
        : base(title, 560)
    {
        AddText(prompt);
        _list = new ListBox { SelectionMode = SelectionMode.Multiple, MaxHeight = 380, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var i in items)
        {
            _list.Items.Add(new CheckBox { Content = i, IsChecked = false, Margin = new Thickness(2) });
        }

        Body.Children.Add(_list);
        AddButton("Cancel", (_, _) => DialogResult = false, isCancel: true);
        AddButton("Add selected", (_, _) => DialogResult = true, primary: true);
    }

    public static IReadOnlyList<string>? Ask(string title, string prompt, IReadOnlyList<string> items)
    {
        var d = new PickListDialog(title, prompt, items);
        if (d.ShowDialog() != true)
        {
            return null;
        }

        return d._list.Items.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Content).ToList();
    }
}
