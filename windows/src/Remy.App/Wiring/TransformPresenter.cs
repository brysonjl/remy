using System.Windows.Threading;
using Remy.App.ViewModels;
using Remy.Core.Contracts;
using Remy.Core.Transforms;

namespace Remy.App.Wiring;

/// <summary>Overlay presentation of Transform runs (v0.2-features.md §8.8.1). Marshals onto the Dispatcher in order;
/// any Show supersedes a pending hide.</summary>
public sealed class TransformPresenter(CompositionRoot root, Dispatcher dispatcher) : ITransformPresenter
{
    int _hideToken;
    OverlayViewModel Vm => root.OverlayVm;

    void Run(Action a) => dispatcher.BeginInvoke(a);

    public void Processing(string name, DestinationTarget? target)
    {
        Interlocked.Increment(ref _hideToken);
        Run(() => { root.Overlay.BeginSession(); Vm.ShowTransforming(name); });
    }

    public void ResumeProcessing(string name)
    {
        Interlocked.Increment(ref _hideToken);
        Run(() => Vm.ShowTransforming(name));
    }

    public void Finished(TransformOutcome outcome)
    {
        switch (outcome.Kind)
        {
            case TransformOutcomeKind.Inserted:
                Show(() => Vm.State = OverlayState.Success, TimeSpan.FromSeconds(1.0));
                break;
            case TransformOutcomeKind.Cancelled:
                Hide(TimeSpan.Zero);
                break;
            case TransformOutcomeKind.Copied when outcome.Notice is null:
                Show(() => { Vm.CopyHint = OverlayViewModel.DefaultCopyHint; Vm.State = OverlayState.CopyOnly; }, TimeSpan.FromSeconds(1.5));
                break;
            case TransformOutcomeKind.NotStarted when outcome.Notice is { Kind: TransformNoticeKind.AlreadyRunning or TransformNoticeKind.DictationBusy } busy:
                // Never replaces an active session's state; shown only on an idle overlay (then it clears itself).
                Interlocked.Increment(ref _hideToken);
                int token = _hideToken;
                Run(() =>
                {
                    if (Vm.State != OverlayState.Hidden) return;
                    root.Overlay.BeginSession();
                    Vm.ShowBusy(busy.Title);
                    _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => { if (token == Volatile.Read(ref _hideToken)) Run(Vm.Hide); });
                });
                break;
            default:
                if (outcome.Notice is { } n) Show(() => Vm.ShowError(n.Title, n.Detail), TimeSpan.FromSeconds(4.0));
                else Hide(TimeSpan.Zero);
                break;
        }
    }

    void Show(Action a, TimeSpan hideAfter)
    {
        int token = Interlocked.Increment(ref _hideToken);
        Run(() => { if (Vm.State == OverlayState.Hidden) root.Overlay.BeginSession(); a(); });
        _ = Task.Delay(hideAfter).ContinueWith(_ => { if (token == Volatile.Read(ref _hideToken)) Run(Vm.Hide); });
    }

    void Hide(TimeSpan delay)
    {
        int token = Interlocked.Increment(ref _hideToken);
        if (delay <= TimeSpan.Zero) { Run(Vm.Hide); return; }
        _ = Task.Delay(delay).ContinueWith(_ => { if (token == Volatile.Read(ref _hideToken)) Run(Vm.Hide); });
    }
}
