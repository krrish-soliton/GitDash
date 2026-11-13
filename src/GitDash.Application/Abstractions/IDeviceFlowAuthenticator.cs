namespace GitDash.Application.Abstractions;

public sealed record DeviceCodePrompt(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    TimeSpan PollInterval,
    TimeSpan ExpiresIn);

/// <summary>OAuth device authorization flow: no client secret, no embedded browser, no redirect listener.</summary>
public interface IDeviceFlowAuthenticator
{
    Task<DeviceCodePrompt> RequestCodeAsync(string clientId, CancellationToken ct);

    /// <summary>Polls until the user approves in the browser. Throws when denied or expired.</summary>
    Task<string> WaitForTokenAsync(string clientId, DeviceCodePrompt prompt, CancellationToken ct);
}
