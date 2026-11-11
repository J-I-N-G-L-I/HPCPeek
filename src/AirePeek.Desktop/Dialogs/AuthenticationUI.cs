using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AirePeek.Core;

namespace AirePeek.Desktop.Dialogs;

public sealed class AuthenticationUI(Window owner, Action<string>? showStatus = null) : IAuthenticationUI
{
    public async Task<IReadOnlyList<string>?> PromptAsync(AuthenticationChallenge challenge, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (challenge.Prompts.Count == 0)
        {
            await Dispatcher.UIThread.InvokeAsync(() => showStatus?.Invoke($"{challenge.Endpoint.Host}:{challenge.Endpoint.Port}\n{challenge.Name}\n{challenge.Instruction}"));
            return Array.Empty<string>();
        }
        return await Dispatcher.UIThread.InvokeAsync(() => ShowPromptAsync(challenge, cancellationToken));
    }

    private async Task<IReadOnlyList<string>?> ShowPromptAsync(AuthenticationChallenge challenge, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new AuthenticationPromptWindow(challenge);
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() => dialog.Close()));
        try { return await dialog.ShowDialog<IReadOnlyList<string>?>(owner); }
        finally { dialog.ClearInputs(); }
    }

    public async Task<bool> ConfirmHostKeyAsync(HostKeyChallenge challenge, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Dispatcher.UIThread.InvokeAsync(() => ShowHostKeyAsync(challenge, cancellationToken));
    }

    private async Task<bool> ShowHostKeyAsync(HostKeyChallenge challenge, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new HostTrustWindow(challenge);
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(false)));
        return await dialog.ShowDialog<bool>(owner);
    }
}

/// <summary>The same prompt window is used for password and every server-defined interactive round.</summary>
internal sealed class AuthenticationPromptWindow : Window
{
    private readonly List<TextBox> inputs = [];

    internal AuthenticationPromptWindow(AuthenticationChallenge challenge)
    {
        DialogUI.Configure(this, $"Verify {challenge.Endpoint.Host}:{challenge.Endpoint.Port}");
        DialogUI.FitContentHeight(this);
        var layout = new Grid { Margin = new Thickness(24), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        layout.Children.Add(DialogUI.Title("Verify sign-in"));
        var body = new StackPanel { Spacing = 16 };
        body.Children.Add(EndpointCard(challenge.Endpoint));
        // Server text is authentication data: retain its wording and options verbatim.
        if (!string.IsNullOrWhiteSpace(challenge.Name) && challenge.Name is not ("password" or "keyboard-interactive"))
            body.Children.Add(DialogUI.Text(challenge.Name, 14));
        if (!string.IsNullOrWhiteSpace(challenge.Instruction))
            body.Children.Add(DialogUI.Text(challenge.Instruction, 13));
        foreach (var prompt in challenge.Prompts)
        {
            var field = new TextBox { PasswordChar = prompt.IsSecret ? '●' : '\0', FontSize = 14 };
            var group = new StackPanel(); DialogUI.Field(group, prompt.Text, field); inputs.Add(field); body.Children.Add(group);
        }
        var scroller = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroller, 1); layout.Children.Add(scroller);
        var cancel = DialogUI.Button("Cancel", () => Close()); cancel.Classes.Add("quiet"); cancel.IsCancel = true;
        var submit = DialogUI.Button("Continue", () =>
        {
            IReadOnlyList<string> answers = inputs.Select(input => input.Text ?? "").ToArray();
            ClearInputs(); Close(answers);
        }, true); submit.IsDefault = true;
        var actions = DialogUI.Actions(cancel, submit); actions.Margin = new Thickness(0, 20, 0, 0);
        Grid.SetRow(actions, 2); layout.Children.Add(actions); Content = layout;
        Opened += (_, _) => inputs.FirstOrDefault()?.Focus();
        Closed += (_, _) => ClearInputs();
    }

    internal void ClearInputs() { foreach (var input in inputs) input.Text = ""; }

    internal static Border EndpointCard(SshEndpoint endpoint)
    {
        var group = new StackPanel { Spacing = 4 };
        group.Children.Add(new TextBlock { Text = $"{endpoint.Host}:{endpoint.Port}", FontSize = 14, FontWeight = FontWeight.Medium, TextWrapping = TextWrapping.Wrap });
        group.Children.Add(DialogUI.Text(endpoint.Username, 12, true));
        return DialogUI.Group(group, new Thickness(12, 10));
    }
}

internal sealed class HostTrustWindow : Window
{
    internal HostTrustWindow(HostKeyChallenge challenge)
    {
        DialogUI.Configure(this, "Verify host fingerprint", 440, 360);
        DialogUI.FitContentHeight(this);
        var layout = new Grid { Margin = new Thickness(24), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        layout.Children.Add(DialogUI.Title("Trust this host?"));
        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(AuthenticationPromptWindow.EndpointCard(challenge.Endpoint));
        body.Children.Add(DialogUI.Text($"SHA-256 · {challenge.Algorithm}", 12, true));
        var fingerprint = new TextBox { Text = challenge.Sha256, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        AutomationProperties.SetName(fingerprint, "SHA-256 host fingerprint"); body.Children.Add(fingerprint);
        body.Children.Add(DialogUI.Text("Compare this fingerprint with a trusted university source before continuing. A changed fingerprint will stop the connection.", 12, true));
        var scroller = new ScrollViewer { Content = body }; Grid.SetRow(scroller, 1); layout.Children.Add(scroller);
        var cancel = DialogUI.Button("Cancel", () => Close(false)); cancel.Classes.Add("quiet"); cancel.IsCancel = true;
        var trust = DialogUI.Button("Trust host", () => Close(true), true);
        var actions = DialogUI.Actions(cancel, trust); actions.Margin = new Thickness(0, 20, 0, 0);
        Grid.SetRow(actions, 2); layout.Children.Add(actions); Content = layout;
    }
}
