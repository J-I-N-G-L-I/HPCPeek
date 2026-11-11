using Avalonia;
using Avalonia.Controls;

namespace AirePeek.Desktop.Dialogs;

/// <summary>A content-sized notice with a reachable action even when the message needs to scroll.</summary>
internal sealed class MessageWindow : Window
{
    internal MessageWindow(string title, string message, bool offerEditConnection = false)
    {
        DialogUI.Configure(this, title);
        DialogUI.FitContentHeight(this, 480);
        var layout = new Grid { Margin = new Thickness(22), RowDefinitions = new("Auto,*,Auto") };
        var heading = DialogUI.Title(title); heading.FontSize = 20; heading.Margin = new Thickness(0, 0, 0, 14);
        layout.Children.Add(heading);
        var body = new ScrollViewer { Content = DialogUI.Text(message), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(body, 1); layout.Children.Add(body);
        var close = DialogUI.Button("Close", () => Close(false), !offerEditConnection); close.IsCancel = true;
        var edit = DialogUI.Button("Edit connection…", () => Close(true), true); edit.IsDefault = offerEditConnection;
        close.IsDefault = !offerEditConnection;
        if (offerEditConnection) close.Classes.Add("quiet");
        var actions = offerEditConnection ? DialogUI.Actions(close, edit) : DialogUI.Actions(close);
        actions.Name = "MessageActions"; actions.Margin = new Thickness(0, 18, 0, 0);
        Grid.SetRow(actions, 2); layout.Children.Add(actions); Content = layout;
    }
}
