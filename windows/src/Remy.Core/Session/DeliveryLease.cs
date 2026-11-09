namespace Remy.Core.Session;

/// <summary>
/// Thread-safe delivery lease (pipeline.md 2.2). Cancel revokes it; every side effect claims it first.
/// Paste is the irreversible boundary: once claimed, cancel is refused and nothing may be retried.
/// </summary>
public sealed class DeliveryLease
{
    readonly object _lock = new();
    bool _revoked, _clipboardClaimed, _copied, _pasteClaimed;

    public bool IsRevoked { get { lock (_lock) return _revoked; } }
    public bool Copied { get { lock (_lock) return _copied; } }
    public bool PasteClaimed { get { lock (_lock) return _pasteClaimed; } }

    /// <summary>Claim initial clipboard ownership once. False when revoked or already claimed.</summary>
    public bool TryClaimClipboard()
    {
        lock (_lock)
        {
            if (_revoked || _clipboardClaimed) return false;
            _clipboardClaimed = true;
            return true;
        }
    }

    /// <summary>Record that the clipboard write succeeded. False if the lease was revoked meanwhile (the text stays on the clipboard).</summary>
    public bool MarkCopied()
    {
        lock (_lock)
        {
            if (!_clipboardClaimed) return false;
            _copied = true;
            return !_revoked;
        }
    }

    /// <summary>Claim the paste right before SendInput. Only when not revoked, copied, and not already claimed.</summary>
    public bool TryClaimPaste()
    {
        lock (_lock)
        {
            if (_revoked || !_copied || _pasteClaimed) return false;
            _pasteClaimed = true;
            return true;
        }
    }

    /// <summary>Cancel: revoke unless the paste was already claimed (then refused and the lease stays valid).</summary>
    public bool TryRevoke()
    {
        lock (_lock)
        {
            if (_pasteClaimed) return false;
            _revoked = true;
            return true;
        }
    }

    /// <summary>Unconditional revoke (shutdown after delivery finished).</summary>
    public (bool Copied, bool PasteClaimed) Revoke()
    {
        lock (_lock) { _revoked = true; return (_copied, _pasteClaimed); }
    }
}
