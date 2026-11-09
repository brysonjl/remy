namespace Remy.Core.Models;

public enum ModelStoreError
{
    Busy, Leased, NotInstalled, IntegrityMismatch, InvalidManifest, InsufficientSpace, Network, Incomplete,
}

/// <summary>Store failure with a verbatim user-facing message (settings-models.md 2.4).</summary>
public sealed class ModelStoreException : Exception
{
    public ModelStoreError Kind { get; }

    public ModelStoreException(ModelStoreError kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;

    public static ModelStoreException Busy() => new(ModelStoreError.Busy, "Another model operation is running.");
    public static ModelStoreException Leased() => new(ModelStoreError.Leased, "Unload the model before switching or removing its assets.");
    public static ModelStoreException NotInstalled() => new(ModelStoreError.NotInstalled, "Verified model assets are not installed.");
    public static ModelStoreException Integrity() => new(ModelStoreError.IntegrityMismatch,
        "Model assets failed integrity verification. Use the explicit model installation controls to repair them.");
    public static ModelStoreException InvalidManifest() => new(ModelStoreError.InvalidManifest,
        "The model manifest is invalid or is not pinned to the expected revision.");
    public static ModelStoreException NoSpace() => new(ModelStoreError.InsufficientSpace,
        "There is not enough free space to stage and verify this model.");
}
