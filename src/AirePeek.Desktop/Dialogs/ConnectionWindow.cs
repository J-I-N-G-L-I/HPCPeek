using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using AirePeek.Core;

namespace AirePeek.Desktop.Dialogs;

public sealed record ConnectionChoice(ConnectionProfile Profile, string ClusterId = ClusterCatalog.AireId);

public sealed class ConnectionWindow : Window
{
    private readonly TextBox host = new() { Name = "TargetHost", PlaceholderText = "Host name" }, port = new() { Name = "TargetPort", Text = "22" }, user = new() { Name = "TargetUsername", PlaceholderText = "University username" };
    private readonly TextBox gatewayHost = new() { Name = "GatewayHost", PlaceholderText = "Gateway host name" }, gatewayPort = new() { Name = "GatewayPort", Text = "22" }, gatewayUser = new() { Name = "GatewayUsername" };
    private readonly CheckBox sharedPassword = new() { Name = "SharePassword", Content = "Use one password for both hosts" };
    private readonly ComboBox cluster = new() { Name = "LoginCluster", Width = 140, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly ComboBox passwordStorage = new() { Name = "PasswordStorage", ItemsSource = new[] { "This connection", "Remember on this device" }, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock passwordHint = DialogUI.Text("", 11, true);
    private readonly TextBlock targetLabel = new() { FontSize = 12, Margin = new Thickness(0, 0, 0, 6) };
    private readonly StackPanel gatewayFields = new() { Spacing = 12, IsVisible = false, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel sharedPasswordOption = new() { Spacing = 3, IsVisible = false };
    private readonly TextBlock error = DialogUI.Text("", 12);
    private readonly Button directTab, gatewayTab;
    private readonly Func<string, ConnectionProfile?>? loadProfile;
    private readonly Dictionary<string, ConnectionDraft> drafts = new(StringComparer.Ordinal);
    private bool useGateway, applying;
    private string previousUsername = "";
    internal string SelectedClusterId { get; private set; }
    private sealed record ClusterOption(string Id, string Name) { public override string ToString() => Name; }
    private sealed record ConnectionDraft(string Host, string Port, string Username, string GatewayHost, string GatewayPort,
        string GatewayUsername, bool UseGateway, bool SharePassword, bool RememberPassword);

    public ConnectionWindow(ConnectionProfile? previous = null, string clusterName = "Aire", Func<string, ConnectionProfile?>? loadProfile = null)
    {
        this.loadProfile = loadProfile;
        SelectedClusterId = ClusterCatalog.All.FirstOrDefault(c => c.Id.Equals(clusterName, StringComparison.OrdinalIgnoreCase)
            || c.Name.Equals(clusterName, StringComparison.OrdinalIgnoreCase))?.Id ?? ClusterCatalog.AireId;
        DialogUI.Configure(this, $"Connect to {ClusterCatalog.Get(SelectedClusterId).Name}", 440, 480);
        DialogUI.FitContentHeight(this);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(24) };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(DialogUI.Title("HPCPeek"));
        cluster.ItemsSource = ClusterCatalog.All.Select(c => new ClusterOption(c.Id, c.Name)).ToArray();
        cluster.SelectedIndex = ClusterCatalog.All.ToList().FindIndex(c => c.Id == SelectedClusterId);
        cluster.VerticalAlignment = VerticalAlignment.Top; cluster.Margin = new Thickness(0, 0, 0, 16);
        AutomationProperties.SetName(cluster, "Cluster to sign in to");
        Grid.SetColumn(cluster, 1); heading.Children.Add(cluster); layout.Children.Add(heading);
        var body = new StackPanel { Spacing = 14 };
        var scroller = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroller, 1); layout.Children.Add(scroller);

        directTab = DialogUI.Button("Direct", () => SetGateway(false));
        gatewayTab = DialogUI.Button("SSH gateway", () => SetGateway(true));
        directTab.Classes.Add("segment"); gatewayTab.Classes.Add("segment");
        var segments = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 3 };
        directTab.HorizontalAlignment = gatewayTab.HorizontalAlignment = HorizontalAlignment.Stretch;
        segments.Children.Add(directTab); Grid.SetColumn(gatewayTab, 1); segments.Children.Add(gatewayTab);
        var segmentGroup = DialogUI.Group(segments, new Thickness(3)); segmentGroup.Classes.Remove("panel"); segmentGroup.Classes.Add("surface");
        body.Children.Add(segmentGroup);
        ToolTip.SetTip(directTab, "Connect on campus or with the university VPN.");
        ToolTip.SetTip(gatewayTab, "Connect through the university SSH gateway.");

        body.Children.Add(HostRow("Host", host, port, targetLabel));
        var account = new StackPanel(); DialogUI.Field(account, "Username", user); body.Children.Add(account);
        gatewayFields.Children.Add(new TextBlock { Text = "Gateway", FontSize = 12, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        gatewayFields.Children.Add(HostRow("Host", gatewayHost, gatewayPort));
        var gatewayAccount = new StackPanel(); DialogUI.Field(gatewayAccount, "Username", gatewayUser); gatewayFields.Children.Add(gatewayAccount);
        sharedPasswordOption.Children.Add(sharedPassword);
        ToolTip.SetTip(sharedPassword, "For accounts using the same password on both hosts. Duo approval remains separate for each host.");
        gatewayFields.Children.Add(sharedPasswordOption); body.Children.Add(gatewayFields);

        var storage = new StackPanel { Spacing = 6 };
        storage.Children.Add(new TextBlock { Text = "Password storage", FontSize = 12 });
        AutomationProperties.SetName(passwordStorage, "Password storage");
        storage.Children.Add(passwordStorage); storage.Children.Add(passwordHint); body.Children.Add(storage);
        passwordStorage.SelectionChanged += (_, _) => UpdatePasswordHint();

        var helpText = DialogUI.Text("Edit your saved MobaXterm session. Copy Remote host, Port and Specify username from SSH. For a gateway, open Network settings → SSH gateway and copy those values too. Use each real host name, not localhost.", 12, true);
        var help = new Border { Child = helpText, Padding = new Thickness(0, 4, 0, 8), IsVisible = false };
        var helpButton = DialogUI.Button("From MobaXterm  ›", () => help.IsVisible = !help.IsVisible);
        helpButton.Classes.Add("quiet"); helpButton.Padding = new Thickness(0, 4); helpButton.MinHeight = 24; helpButton.HorizontalAlignment = HorizontalAlignment.Left;
        body.Children.Add(helpButton); body.Children.Add(help);
        error.Classes.Add("attention"); error.IsVisible = false; body.Children.Add(error);

        var cancel = DialogUI.Button("Cancel", () => Close()); cancel.Classes.Add("quiet"); cancel.IsCancel = true;
        var connect = DialogUI.Button("Connect", Connect, true); connect.IsDefault = true;
        var actions = DialogUI.Actions(cancel, connect); actions.Margin = new Thickness(0, 20, 0, 0);
        Grid.SetRow(actions, 2); layout.Children.Add(actions); Content = layout;
        user.PropertyChanged += (_, args) =>
        {
            if (args.Property != TextBox.TextProperty || applying) return;
            // Keep the common account convenient without overwriting a distinct gateway account.
            if (string.IsNullOrEmpty(gatewayUser.Text) || gatewayUser.Text == previousUsername) gatewayUser.Text = user.Text;
            previousUsername = user.Text ?? ""; UpdateSharedPassword();
        };
        gatewayUser.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty && !applying) UpdateSharedPassword(); };
        cluster.SelectionChanged += (_, _) =>
        {
            if (!applying && cluster.SelectedItem is ClusterOption selected) SelectCluster(selected.Id);
        };
        var initial = previous ?? loadProfile?.Invoke(SelectedClusterId);
        var fallbackProfile = ClusterCatalog.All.Where(c => c.Id != SelectedClusterId).Select(c => loadProfile?.Invoke(c.Id)).FirstOrDefault(p => p is not null);
        ApplyDraft(CreateDraft(SelectedClusterId, initial, fallbackProfile is null ? null : CreateDraft(SelectedClusterId, fallbackProfile, null)));
        Opened += (_, _) => { if (string.IsNullOrWhiteSpace(user.Text)) user.Focus(); else connect.Focus(); };
    }

    internal void SelectCluster(string id)
    {
        id = ClusterCatalog.Get(id).Id;
        if (id == SelectedClusterId) return;
        var fallback = CaptureDraft(); drafts[SelectedClusterId] = fallback;
        SelectedClusterId = id;
        ApplyDraft(drafts.GetValueOrDefault(id) ?? CreateDraft(id, loadProfile?.Invoke(id), fallback));
        error.IsVisible = false;
    }

    private static string DefaultHost(string id) => id == ClusterCatalog.CalderId ? "calder.leeds.ac.uk" : "aire.leeds.ac.uk";
    private static ConnectionDraft CreateDraft(string id, ConnectionProfile? profile, ConnectionDraft? fallback)
    {
        string username = profile?.Target.Username ?? fallback?.Username ?? "";
        return new(
            string.IsNullOrWhiteSpace(profile?.Target.Host) ? DefaultHost(id) : profile.Target.Host,
            (profile?.Target.Port ?? 22).ToString(), username,
            profile?.Gateway?.Host ?? fallback?.GatewayHost ?? "rash.leeds.ac.uk",
            profile?.Gateway?.Port.ToString() ?? fallback?.GatewayPort ?? "22",
            profile?.Gateway?.Username ?? fallback?.GatewayUsername ?? username,
            profile is null ? fallback?.UseGateway ?? false : profile.Gateway is not null,
            profile?.ReuseGatewayPassword ?? false, profile?.RememberPassword ?? false);
    }
    private ConnectionDraft CaptureDraft() => new(host.Text ?? "", port.Text ?? "", user.Text ?? "",
        gatewayHost.Text ?? "", gatewayPort.Text ?? "", gatewayUser.Text ?? "", useGateway,
        sharedPassword.IsChecked == true, passwordStorage.SelectedIndex == 1);
    private void ApplyDraft(ConnectionDraft draft)
    {
        applying = true;
        try
        {
            host.Text = draft.Host; port.Text = draft.Port; user.Text = draft.Username;
            gatewayHost.Text = draft.GatewayHost; gatewayPort.Text = draft.GatewayPort; gatewayUser.Text = draft.GatewayUsername;
            previousUsername = draft.Username;
            sharedPassword.IsChecked = draft.SharePassword;
            passwordStorage.SelectedIndex = draft.RememberPassword ? 1 : 0;
            cluster.SelectedIndex = ClusterCatalog.All.ToList().FindIndex(c => c.Id == SelectedClusterId);
            var name = ClusterCatalog.Get(SelectedClusterId).Name;
            Title = $"Connect to {name}"; targetLabel.Text = $"{name} host";
            AutomationProperties.SetName(host, targetLabel.Text);
            SetGateway(draft.UseGateway); UpdatePasswordHint();
        }
        finally { applying = false; }
    }

    private static Grid HostRow(string label, TextBox hostField, TextBox portField, TextBlock? hostLabel = null)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,68"), ColumnSpacing = 12 };
        var hostPanel = new StackPanel();
        if (hostLabel is null) DialogUI.Field(hostPanel, label, hostField);
        else { hostLabel.Text = label; hostPanel.Children.Add(hostLabel); hostPanel.Children.Add(hostField); hostField.MinHeight = 34; }
        row.Children.Add(hostPanel);
        var portPanel = new StackPanel(); DialogUI.Field(portPanel, "Port", portField); Grid.SetColumn(portPanel, 1); row.Children.Add(portPanel);
        return row;
    }

    private void SetGateway(bool enabled)
    {
        useGateway = enabled; gatewayFields.IsVisible = enabled;
        directTab.Classes.Set("selected", !enabled); gatewayTab.Classes.Set("selected", enabled);
        AutomationProperties.SetName(directTab, enabled ? "Direct" : "Direct, selected");
        AutomationProperties.SetName(gatewayTab, enabled ? "SSH gateway, selected" : "SSH gateway");
        if (enabled && string.IsNullOrWhiteSpace(gatewayUser.Text)) gatewayUser.Text = user.Text;
        UpdateSharedPassword();
        DialogUI.FitContentHeight(this);
        if (IsVisible) DialogUI.CenterOnScreen(this);
    }

    private void UpdateSharedPassword()
    {
        var sameUser = !string.IsNullOrWhiteSpace(user.Text) && string.Equals(user.Text.Trim(), gatewayUser.Text?.Trim(), StringComparison.Ordinal);
        sharedPasswordOption.IsVisible = useGateway && sameUser;
        if (!sameUser) sharedPassword.IsChecked = false;
    }
    private void UpdatePasswordHint() => passwordHint.Text = passwordStorage.SelectedIndex == 1
        ? "Saved in this computer's secure credential store. Duo is still required."
        : "Password stays in memory for this connection. Duo is still required.";

    private void Connect()
    {
        try { Close(new ConnectionChoice(ReadProfile(), SelectedClusterId)); }
        catch (ArgumentException ex) { error.Text = ex.Message; error.IsVisible = true; }
    }

    internal ConnectionProfile ReadProfile()
    {
        var target = Endpoint(host.Text, port.Text, user.Text);
        var jump = useGateway ? Endpoint(gatewayHost.Text, gatewayPort.Text, gatewayUser.Text) : null;
        return new(target, jump, jump is not null && sharedPasswordOption.IsVisible && sharedPassword.IsChecked == true, passwordStorage.SelectedIndex == 1);
    }

    internal static SshEndpoint Endpoint(string? host, string? port, string? user)
    {
        host = host?.Trim() ?? ""; user = user?.Trim() ?? "";
        if (host.Length == 0 || host.Any(char.IsWhiteSpace) || host.Contains('/') || host.Contains('\\') || host.Any(char.IsControl))
            throw new ArgumentException("Enter a hostname or IP address without ssh://.");
        if (user.Length == 0 || user.Any(char.IsControl)) throw new ArgumentException("Enter the username for this host.");
        if (!int.TryParse(port, out var number) || number is < 1 or > 65535) throw new ArgumentException("Port must be between 1 and 65535.");
        return new(host, number, user);
    }
}
