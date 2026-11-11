using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AirePeek.Desktop.Infrastructure;
using AirePeek.Core;
using AirePeek.Ssh;
using System.Globalization;

namespace AirePeek.Desktop.Dialogs;

public enum SettingsAction { None, Connect }
public sealed record SettingsResult(SettingsAction Action, string? ClusterId = null);

public sealed class SettingsWindow : Window
{
    private readonly SettingsStore store;
    private readonly ComboBox cluster = new() { Name = "ConnectionCluster", Width = 150, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly StackPanel connectionSummary = new() { Spacing = 5 };
    private string selectedClusterId;
    private readonly ComboBox theme = new() { ItemsSource = new[] { "System", "Light", "Dark" }, Width = 150, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly CheckBox pinned = new() { Content = "Keep panel pinned" };
    private readonly CheckBox notifications = new() { Content = "Job notifications" };
    private readonly ComboBox refresh = new() { Name = "AutoRefresh", ItemsSource = new[] { "5 minutes", "15 minutes", "30 minutes", "60 minutes", "Custom…" }, Width = 150, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBox customSeconds = new() { Name = "CustomRefreshSeconds", Width = 150, MinHeight = 34, HorizontalAlignment = HorizontalAlignment.Right, PlaceholderText = "15–86400" };
    private readonly StackPanel customRefresh = new() { Spacing = 5 };
    private static readonly int[] RefreshPresets = [300, 900, 1800, 3600];
    private readonly TextBlock feedback = DialogUI.Text("", 12, true);
    private readonly ContentControl page = new();
    private readonly List<Button> sectionButtons = [];
    private readonly List<Control> pages = [];

    public SettingsWindow(SettingsStore store)
    {
        this.store = store;
        selectedClusterId = ClusterCatalog.Get(store.Value.SelectedClusterId).Id;
        DialogUI.Configure(this, "HPCPeek Settings", 480, 500);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"), Margin = new Thickness(24) };
        layout.Children.Add(DialogUI.Title("Settings"));
        var segments = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 3 };
        var labels = new[] { "Connection", "Appearance", "Notifications" };
        for (var i = 0; i < labels.Length; i++)
        {
            var index = i;
            var button = DialogUI.Button(labels[i], () => SelectSection(index)); button.Classes.Add("segment");
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.Padding = new Thickness(6, 5);
            Grid.SetColumn(button, i); segments.Children.Add(button); sectionButtons.Add(button);
        }
        var segmentGroup = DialogUI.Group(segments, new Thickness(3)); segmentGroup.Classes.Remove("panel"); segmentGroup.Classes.Add("surface");
        Grid.SetRow(segmentGroup, 1); layout.Children.Add(segmentGroup);
        pages.Add(ConnectionPage()); pages.Add(AppearancePage()); pages.Add(NotificationPage());
        page.Margin = new Thickness(0, 20, 0, 0); Grid.SetRow(page, 2); layout.Children.Add(page);
        feedback.IsVisible = false; feedback.Margin = new Thickness(0, 8, 0, 0); Grid.SetRow(feedback, 3); layout.Children.Add(feedback);
        var cancel = DialogUI.Button("Cancel", () => Close()); cancel.Classes.Add("quiet"); cancel.IsCancel = true;
        var apply = DialogUI.Button("Apply", () => SaveAndClose(SettingsAction.None), true); apply.IsDefault = true;
        var actions = DialogUI.Actions(cancel, apply); actions.Margin = new Thickness(0, 20, 0, 0);
        Grid.SetRow(actions, 4); layout.Children.Add(actions); Content = layout;
        SelectSection(0);
        ReloadPreferences();
    }

    internal void SelectSection(int index)
    {
        page.Content = pages[index];
        for (var i = 0; i < sectionButtons.Count; i++)
        {
            sectionButtons[i].Classes.Set("selected", i == index);
            Avalonia.Automation.AutomationProperties.SetName(sectionButtons[i], $"{sectionButtons[i].Content}{(i == index ? ", selected" : "")}");
        }
    }

    private Control ConnectionPage()
    {
        var panel = new StackPanel { Spacing = 14 };
        var clusterRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var clusterLabel = DialogUI.Text("Cluster"); clusterLabel.VerticalAlignment = VerticalAlignment.Center; clusterRow.Children.Add(clusterLabel);
        cluster.ItemsSource = ClusterCatalog.All.Select(item => item.Name).ToArray();
        cluster.SelectedIndex = ClusterCatalog.All.Select(item => item.Id).ToList().IndexOf(selectedClusterId);
        cluster.SelectionChanged += (_, _) =>
        {
            if (cluster.SelectedIndex < 0) return;
            selectedClusterId = ClusterCatalog.All[cluster.SelectedIndex].Id;
            UpdateConnectionSummary();
        };
        Avalonia.Automation.AutomationProperties.SetName(cluster, "Cluster connection to edit");
        Grid.SetColumn(cluster, 1); clusterRow.Children.Add(cluster); panel.Children.Add(clusterRow);
        UpdateConnectionSummary();
        panel.Children.Add(DialogUI.Group(connectionSummary));
        var edit = DialogUI.Button("Edit connection…", () => SaveAndClose(SettingsAction.Connect)); edit.HorizontalAlignment = HorizontalAlignment.Left; panel.Children.Add(edit);
        var forget = new Button { Content = "Forget saved passwords", Name = "ForgetSavedPasswords", HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 28, Padding = new Thickness(0, 4) };
        forget.Classes.Add("quiet"); forget.Click += async (_, _) => await ForgetPasswordsAsync(); panel.Children.Add(forget);
        ToolTip.SetTip(forget, "Remove remembered passwords for this cluster's target and gateway from this device. Shared gateway credentials also affect other cluster logins. Trusted host fingerprints are kept.");
        var refreshRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var refreshLabel = DialogUI.Text("Auto refresh"); refreshLabel.VerticalAlignment = VerticalAlignment.Center; refreshRow.Children.Add(refreshLabel);
        Grid.SetColumn(refresh, 1); refreshRow.Children.Add(refresh); panel.Children.Add(refreshRow);
        Avalonia.Automation.AutomationProperties.SetName(refresh, "Auto refresh interval for jobs and GPUs");
        ToolTip.SetTip(refresh, "Updates jobs and GPUs for connected clusters. Changes apply without reconnecting.");
        refresh.SelectionChanged += (_, _) => customRefresh.IsVisible = refresh.SelectedIndex == 4;
        var customRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var customLabel = DialogUI.Text("Seconds"); customLabel.VerticalAlignment = VerticalAlignment.Center; customRow.Children.Add(customLabel);
        Grid.SetColumn(customSeconds, 1); customRow.Children.Add(customSeconds); customRefresh.Children.Add(customRow);
        customRefresh.Children.Add(DialogUI.Text("15 seconds to 24 hours.", 12, true)); panel.Children.Add(customRefresh);
        Avalonia.Automation.AutomationProperties.SetName(customSeconds, "Custom refresh interval in seconds, 15 to 86400");
        var location = new StackPanel { Spacing = 8, IsVisible = false };
        location.Children.Add(new SelectableTextBlock { Text = store.DirectoryPath, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        location.Children.Add(DialogUI.Text("Settings contain no passwords. Remembered passwords stay in this device's credential store. Duo responses are never saved.", 12, true));
        var storage = DialogUI.Button("Settings location  ›", () => location.IsVisible = !location.IsVisible); storage.Classes.Add("quiet"); storage.Padding = new Thickness(0, 4); storage.MinHeight = 24; storage.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(storage); panel.Children.Add(location);
        return new ScrollViewer { Content = panel };
    }
    private async Task ForgetPasswordsAsync()
    {
        var preferences = store.GetCluster(selectedClusterId);
        if (preferences.Connection is not { } profile) { ShowFeedback("No connection saved for this cluster."); return; }
        try
        {
            preferences.Connection = profile with { RememberPassword = false }; store.Save();
            await Task.Run(() => PasswordVault.Forget(profile));
            ShowFeedback("Saved passwords removed from this device. The current connection stays open.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { ShowFeedback("Could not remove saved passwords. Use Windows Credential Manager or macOS Keychain Access to remove HPCPeek credentials."); }
    }
    private void UpdateConnectionSummary()
    {
        var profile = store.GetCluster(selectedClusterId).Connection;
        connectionSummary.Children.Clear();
        connectionSummary.Children.Add(DialogUI.Text(profile is null ? "No connection saved" : $"{profile.Target.Host}:{profile.Target.Port}", 15));
        if (profile is not null) connectionSummary.Children.Add(DialogUI.Text(profile.Target.Username, 12, true));
        if (profile?.Gateway is { } gateway) connectionSummary.Children.Add(DialogUI.Text($"via {gateway.Host}:{gateway.Port}", 12, true));
        else if (profile is not null) connectionSummary.Children.Add(DialogUI.Text("Direct connection", 12, true));
    }
    private Control AppearancePage()
    {
        var panel = new StackPanel { Spacing = 12 };
        var themeRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var label = DialogUI.Text("Theme"); label.VerticalAlignment = VerticalAlignment.Center; themeRow.Children.Add(label); Grid.SetColumn(theme, 1); themeRow.Children.Add(theme); panel.Children.Add(themeRow);
        var pinGroup = new StackPanel { Spacing = 3 };
        pinGroup.Children.Add(pinned); pinGroup.Children.Add(new TextBlock { Text = "Unpinned panels hide when you switch apps.", FontSize = 12, Margin = new Thickness(25, 0, 0, 0), Classes = { "muted" } }); panel.Children.Add(pinGroup);
        ToolTip.SetTip(pinned, "Hiding the panel keeps sampling. Escape collapses details, then hides the panel.");
        panel.Children.Add(DialogUI.Divider());
        var transferRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var transferLabel = DialogUI.Text("Preferences"); transferLabel.VerticalAlignment = VerticalAlignment.Center; transferRow.Children.Add(transferLabel);
        var import = new Button { Content = "Import…" }; import.Click += async (_, _) => await ImportAsync();
        var export = new Button { Content = "Export…" }; export.Click += async (_, _) => await ExportAsync();
        var transfers = DialogUI.Actions(import, export); Grid.SetColumn(transfers, 1); transferRow.Children.Add(transfers); panel.Children.Add(transferRow);
        ToolTip.SetTip(transfers, "Transfer appearance, refresh interval, notifications and GPU filters. Account details and trusted hosts are excluded.");
        return new ScrollViewer { Content = panel };
    }
    private Control NotificationPage()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(notifications);
        panel.Children.Add(new TextBlock { Text = "Started, completed, failed or timed out.", FontSize = 12, Margin = new Thickness(25, 0, 0, 0), Classes = { "muted" } });
        panel.Children.Add(DialogUI.Text("First connection and reconnect stay quiet. Results are confirmed with Slurm before notifying you.", 12, true));
        ToolTip.SetTip(notifications, "Notices close after eight seconds, pause while hovered and never take focus. Copy a job ID or open its details. Unconfirmed results stay in My jobs.");
        return panel;
    }
    private void ReloadPreferences()
    {
        selectedClusterId = ClusterCatalog.Get(store.Value.SelectedClusterId).Id;
        cluster.SelectedIndex = ClusterCatalog.All.Select(item => item.Id).ToList().IndexOf(selectedClusterId);
        UpdateConnectionSummary();
        theme.SelectedIndex = store.Value.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        pinned.IsChecked = store.Value.IsPinned;
        notifications.IsChecked = store.Value.Notifications;
        var preset = Array.IndexOf(RefreshPresets, store.Value.RefreshIntervalSeconds);
        refresh.SelectedIndex = preset < 0 ? 4 : preset;
        customSeconds.Text = store.Value.RefreshIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        customRefresh.IsVisible = preset < 0;
    }
    private void SavePreferences()
    {
        int interval;
        if (refresh.SelectedIndex is >= 0 and < 4) interval = RefreshPresets[refresh.SelectedIndex];
        else if (!int.TryParse(customSeconds.Text, NumberStyles.None, CultureInfo.InvariantCulture, out interval) || interval is < 15 or > 86400)
            throw new InvalidDataException("Enter a whole number from 15 to 86400 seconds.");
        store.Value.Theme = theme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
        store.Value.IsPinned = pinned.IsChecked == true;
        store.Value.Notifications = notifications.IsChecked == true;
        store.Value.RefreshIntervalSeconds = interval;
        store.Save();
    }
    private void SaveAndClose(SettingsAction action)
    {
        try { SavePreferences(); Close(new SettingsResult(action, selectedClusterId)); }
        catch (InvalidDataException ex) { SelectSection(0); ShowFeedback(ex.Message); customSeconds.Focus(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowFeedback("Settings could not be saved. Choose a writable application folder."); }
    }
    private void ShowFeedback(string message) { feedback.Text = message; feedback.IsVisible = true; }
    private async Task ExportAsync()
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export non-sensitive preferences", SuggestedFileName = "hpcpeek-preferences.json", DefaultExtension = "json",
                FileTypeChoices = new[] { new FilePickerFileType("JSON preferences") { Patterns = new[] { "*.json" } } }
            });
            if (file is null) return;
            SavePreferences();
            await using var stream = await file.OpenWriteAsync(); stream.SetLength(0); await store.ExportAsync(stream);
            ShowFeedback("Preferences exported.");
        }
        catch (InvalidDataException ex) { SelectSection(0); ShowFeedback(ex.Message); customSeconds.Focus(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { ShowFeedback("Could not export preferences to that location."); }
    }
    private async Task ImportAsync()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import preferences", AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("JSON preferences") { Patterns = new[] { "*.json" } } }
            });
            if (files.Count == 0) return;
            await using var stream = await files[0].OpenReadAsync(); await store.ImportAsync(stream); ReloadPreferences();
            ShowFeedback("Preferences imported.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException) { ShowFeedback("This preference file could not be imported."); }
    }
}
