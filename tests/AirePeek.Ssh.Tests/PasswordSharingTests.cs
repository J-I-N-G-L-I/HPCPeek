using System.Reflection;
using AirePeek.Core;

namespace AirePeek.Ssh.Tests;

public sealed class PasswordSharingTests
{
    private static readonly SshEndpoint Target = new("aire.example.edu", 22, "student");
    private static readonly SshEndpoint Gateway = new("gateway.example.edu", 2222, "student");
    private static AuthenticationChallenge Password(SshEndpoint endpoint, string name = "keyboard-interactive", string instruction = "") =>
        new(endpoint, name, instruction, [new("Password:", true)]);

    [Fact]
    public async Task OptInReplaysPasswordOnceButKeepsEachHostsDuoInteractive()
    {
        var ui = new RecordingUi();
        using var sharing = new ConnectionPasswordSharing(new(Target, Gateway, true), ui);
        await sharing.PromptAsync(Password(Gateway), default);
        Assert.True(sharing.HasCachedPassword);
        await sharing.PromptAsync(new(Gateway, "keyboard-interactive", "Approve in Duo Mobile", [new("Choose 7 or 9", false)]), default);
        sharing.GatewayAuthenticated();
        var targetAnswers = await sharing.PromptAsync(Password(Target), default);
        Assert.Equal("synthetic-password", Assert.Single(targetAnswers!));
        Assert.True(sharing.TargetPasswordReused);
        Assert.False(sharing.HasCachedPassword);
        await sharing.PromptAsync(new(Target, "keyboard-interactive", "Approve in Duo Mobile", [new("Choose 7 or 9", false)]), default);
        Assert.Equal(3, ui.Challenges.Count);
        Assert.Equal(2, ui.Challenges.Count(c => c.Instruction.Contains("Duo")));
        // A server retry asks the human again. A failed shared response is never replayed in a loop.
        await sharing.PromptAsync(Password(Target), default);
        Assert.Equal(4, ui.Challenges.Count);
        Assert.Equal(Target, ui.Challenges[^1].Endpoint);
    }

    [Theory]
    [InlineData(false, "student")]
    [InlineData(true, "another-account")]
    public async Task DefaultOrDifferentUsernamesNeverCache(bool enabled, string targetUser)
    {
        var target = Target with { Username = targetUser };
        var ui = new RecordingUi();
        using var sharing = new ConnectionPasswordSharing(new(target, Gateway, enabled), ui);
        await sharing.PromptAsync(Password(Gateway), default);
        sharing.GatewayAuthenticated();
        await sharing.PromptAsync(Password(target), default);
        Assert.Equal(2, ui.Challenges.Count);
        Assert.False(sharing.HasCachedPassword);
        Assert.False(sharing.TargetPasswordReused);
    }

    [Theory]
    [InlineData("Passcode or option", "")]
    [InlineData("Verification code:", "")]
    [InlineData("Password:", "Enter the one-time password")]
    [InlineData("Password:", "Duo verification")]
    [InlineData("Password:", "Enter your security code")]
    [InlineData("Password:", "Authenticator token")]
    [InlineData("Password:", "输入验证码")]
    [InlineData("Secret:", "")]
    public async Task HiddenFactorsAndAmbiguousPasswordsStayInteractive(string label, string instruction)
    {
        var ui = new RecordingUi();
        using var sharing = new ConnectionPasswordSharing(new(Target, Gateway, true), ui);
        await sharing.PromptAsync(new(Gateway, "keyboard-interactive", instruction, [new(label, true)]), default);
        Assert.False(sharing.HasCachedPassword);
        sharing.GatewayAuthenticated();
        await sharing.PromptAsync(Password(Target), default);
        Assert.Equal(2, ui.Challenges.Count);
        Assert.False(sharing.TargetPasswordReused);
    }

    [Fact]
    public async Task CachedPasswordDoesNotAnswerTargetOtpOrMultiplePrompts()
    {
        var ui = new RecordingUi();
        using var sharing = new ConnectionPasswordSharing(new(Target, Gateway, true), ui);
        await sharing.PromptAsync(Password(Gateway, "password"), default);
        sharing.GatewayAuthenticated();
        await sharing.PromptAsync(new(Target, "keyboard-interactive", "Duo", [new("Password:", true)]), default);
        await sharing.PromptAsync(new(Target, "keyboard-interactive", "", [new("Password:", true), new("OTP:", true)]), default);
        Assert.Equal(3, ui.Challenges.Count);
        Assert.False(sharing.TargetPasswordReused);
    }

    [Fact]
    public async Task DisposalZerosOwnedBufferAndFollowingConnectionRequiresFreshPassword()
    {
        var ui = new RecordingUi();
        var sharing = new ConnectionPasswordSharing(new(Target, Gateway, true), ui);
        await sharing.PromptAsync(Password(Gateway), default);
        var owned = (char[])typeof(ConnectionPasswordSharing).GetField("password", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sharing)!;
        sharing.Dispose();
        Assert.All(owned, c => Assert.Equal('\0', c));
        Assert.False(sharing.HasCachedPassword);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => sharing.PromptAsync(Password(Target), default));
        using var next = new ConnectionPasswordSharing(new(Target, Gateway, true), ui);
        next.GatewayAuthenticated();
        await next.PromptAsync(Password(Target), default);
        Assert.Equal(2, ui.Challenges.Count);
        Assert.False(next.TargetPasswordReused);
    }

    [Fact]
    public async Task CancellationDoesNotReplayPassword()
    {
        var ui = new RecordingUi();
        using var sharing = new ConnectionPasswordSharing(new(Target, Gateway, true), ui);
        await sharing.PromptAsync(Password(Gateway), default);
        sharing.GatewayAuthenticated();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sharing.PromptAsync(Password(Target), cancellation.Token));
        Assert.False(sharing.TargetPasswordReused);
    }

    [Fact]
    public async Task LateUiCompletionCannotRepopulateDisposedCache()
    {
        var pending = new DeferredUi();
        var sharing = new ConnectionPasswordSharing(new(Target, Gateway, true), pending);
        var challenge = sharing.PromptAsync(Password(Gateway), default);
        sharing.Dispose();
        pending.Response.SetResult(["synthetic-password"]);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => challenge);
        Assert.False(sharing.HasCachedPassword);
    }

    private sealed class DeferredUi : IAuthenticationUI
    {
        public TaskCompletionSource<IReadOnlyList<string>?> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<string>?> PromptAsync(AuthenticationChallenge challenge, CancellationToken cancellationToken) => Response.Task;
        public Task<bool> ConfirmHostKeyAsync(HostKeyChallenge challenge, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class RecordingUi : IAuthenticationUI
    {
        public List<AuthenticationChallenge> Challenges { get; } = [];
        public Task<IReadOnlyList<string>?> PromptAsync(AuthenticationChallenge challenge, CancellationToken cancellationToken)
        {
            Challenges.Add(challenge);
            return Task.FromResult<IReadOnlyList<string>?>(challenge.Prompts.Select(p => p.Text.StartsWith("Password") ? "synthetic-password" : "synthetic-response").ToArray());
        }
        public Task<bool> ConfirmHostKeyAsync(HostKeyChallenge challenge, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
