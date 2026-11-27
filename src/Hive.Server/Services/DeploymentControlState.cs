namespace Hive.Server.Services;

public sealed record DeploymentControlSnapshot(
    bool DeploymentsPaused,
    string? PauseReason,
    DateTimeOffset? PausedAtUtc,
    string? PausedBySid);

/// <summary>
/// Thread-safe singleton tracking emergency-stop deployment pause state (V23).
/// </summary>
public sealed class DeploymentControlState
{
    private readonly object _syncRoot = new();
    private bool _deploymentsPaused;
    private string? _pauseReason;
    private DateTimeOffset? _pausedAtUtc;
    private string? _pausedBySid;

    public bool DeploymentsPaused
    {
        get
        {
            lock (_syncRoot)
            {
                return _deploymentsPaused;
            }
        }
    }

    public string? PauseReason
    {
        get
        {
            lock (_syncRoot)
            {
                return _pauseReason;
            }
        }
    }

    public DateTimeOffset? PausedAtUtc
    {
        get
        {
            lock (_syncRoot)
            {
                return _pausedAtUtc;
            }
        }
    }

    public string? PausedBySid
    {
        get
        {
            lock (_syncRoot)
            {
                return _pausedBySid;
            }
        }
    }

    public DeploymentControlSnapshot Update(bool pause, string? reason, string actorSid, DateTimeOffset utcNow)
    {
        lock (_syncRoot)
        {
            _deploymentsPaused = pause;
            if (pause)
            {
                _pauseReason = string.IsNullOrWhiteSpace(reason) ? "Emergency stop activated." : reason.Trim();
                _pausedAtUtc = utcNow;
                _pausedBySid = actorSid;
            }
            else
            {
                _pauseReason = null;
                _pausedAtUtc = null;
                _pausedBySid = null;
            }

            return new DeploymentControlSnapshot(_deploymentsPaused, _pauseReason, _pausedAtUtc, _pausedBySid);
        }
    }

    public DeploymentControlSnapshot GetSnapshot()
    {
        lock (_syncRoot)
        {
            return new DeploymentControlSnapshot(_deploymentsPaused, _pauseReason, _pausedAtUtc, _pausedBySid);
        }
    }
}
