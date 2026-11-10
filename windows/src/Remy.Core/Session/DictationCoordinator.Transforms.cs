using Remy.Core.Contracts;
using Remy.Core.Diagnostics;
using Remy.Core.Transforms;

namespace Remy.Core.Session;

/// <summary>Transform shortcut dispatch and hook configuration (v0.2-features.md §8.6-8.7).</summary>
public sealed partial class DictationCoordinator
{
    string? _transformSignature;

    /// <summary>The runner can still be cancelled (reading, processing, or delivering before the paste is claimed).</summary>
    bool TransformCanCancel => _d.Transforms?.CanCancel == true;

    /// <summary>Idempotent: the hook is reconfigured only when the active transform bindings changed.</summary>
    void ConfigureTransformHotkeys()
    {
        if (_d.Hotkeys is not ITransformHotkeys hooks || _d.Transforms is null) return;
        var bindings = _transformStore.ActiveShortcutBindings;
        var sig = string.Join(";", bindings.Select(b => $"{b.Id}/{b.VirtualKey}/{(int)b.Modifiers}"));
        lock (_gate) { if (sig == _transformSignature) return; _transformSignature = sig; }
        try { hooks.ConfigureTransforms(bindings); }
        catch (Exception ex) { RemyLog.Error("Transform", "hotkey configure failed", ex); }
    }

    void OnTransformPressed(object? sender, Guid id)
    {
        if (_sleeping || _shutdown) return;
        var runner = _d.Transforms;
        if (runner is null) return;
        var transform = _transformStore.Transform(id);
        if (transform is not { IsEnabled: true }) return;
        _ = Guard(() => runner.RunAsync(transform.Instruction, IsDictationBusy), "transform");
    }

    void OnTransformPhaseChanged(object? sender, EventArgs e) => RaiseStateChanged();
}
