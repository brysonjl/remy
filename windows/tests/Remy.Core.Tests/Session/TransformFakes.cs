using Remy.Core.Contracts;
using Remy.Core.Hotkeys;
using Remy.Core.Recovery;
using Remy.Core.Session;
using Remy.Core.Settings;
using Remy.Core.Transforms;

namespace Remy.Core.Tests.Session;

/// <summary>Hook fake that also implements the transform seam.</summary>
sealed class FakeTransformHotkeys : IHotkeyListener, ITransformHotkeys
{
    public event EventHandler<HotkeyBinding>? BindingPressed { add { } remove { } }
    public event EventHandler<HotkeyBinding>? BindingReleased { add { } remove { } }
    public event EventHandler<double?>? CancelProgressChanged { add { } remove { } }
    public event EventHandler? CancelRequested;
    public event EventHandler<Guid>? TransformPressed;
    public bool CancellationEnabled { get; set; }
    public List<IReadOnlyList<HotkeyBinding>> TransformConfigs { get; } = [];
    public void Configure(IReadOnlyList<HotkeyBinding> recordingBindings, HotkeyBinding? cancelShortcut, bool holdToCancel) { }
    public void ConfigureTransforms(IReadOnlyList<HotkeyBinding> bindings) => TransformConfigs.Add(bindings);
    public void Start() { }
    public void Stop() { }
    public void RequireNeutralModifiers() { }
    public void ResetCancellationGesture() { }
    public Task<HotkeyBinding?> RecordShortcutAsync(bool allowEscape, CancellationToken ct) => Task.FromResult<HotkeyBinding?>(null);
    public void Dispose() { }
    public void PressTransform(Guid id) => TransformPressed?.Invoke(this, id);
    public void RaiseCancel() => CancelRequested?.Invoke(this, EventArgs.Empty);
}

/// <summary>Runner harness (pid 4242, selection "  rough text\n", provider returns "Clearer text.").</summary>
sealed class TransformRig
{
    public string SelectionText = "  rough text\n";
    public TransformReadResult? ReadOverride;
    public TransformRouteResult RouteResult;
    public Func<TransformRequest, CancellationToken, Task<string>> Provider;
    public bool FocusUnchanged = true, SelectionMatches = true;
    public Func<TransformDeliveryResult> DeliverResult = () => TransformDeliveryResult.Success;
    public TaskCompletionSource? DeliverGate;
    public int Reads, Requests, Deliveries;
    public readonly List<TransformRequest> Sent = [];
    public readonly List<(string Text, bool Paste)> Delivered = [];
    public readonly List<TransformEvent> Events = [];
    public readonly DestinationTarget Target = new(1, 4242, "notepad.exe", null, "Untitled", "1", true, false, false, false);
    public TransformRunner Runner { get; }

    public TransformRig()
    {
        Provider = (_, _) => Task.FromResult("Clearer text.");
        RouteResult = TransformRouteResult.Ready(new TransformRoute("Fake", (r, ct) =>
        {
            Requests++; Sent.Add(r);
            return Provider(r, ct);
        }));
        Runner = new TransformRunner(new TransformRunnerDependencies
        {
            Route = () => RouteResult,
            ReadSelection = _ =>
            {
                Reads++;
                return Task.FromResult(ReadOverride ?? TransformReadResult.Ok(new TransformSelection(SelectionText, Target)));
            },
            SelectionStillMatches = (_, _) => Task.FromResult(SelectionMatches),
            IsFocusUnchanged = _ => FocusUnchanged,
            Deliver = async (text, sel, paste, lease) =>
            {
                Deliveries++; Delivered.Add((text, paste));
                if (DeliverGate != null) await DeliverGate.Task;
                return DeliverResult();
            },
            OnEvent = e => { lock (Events) Events.Add(e); },
        });
    }

    public static readonly TransformInstruction Polish = new("Polish", "Polish it.");

    public static async Task WaitFor(Func<bool> cond, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not reached");
            await Task.Delay(5);
        }
    }
}

/// <summary>Coordinator over the shared fakes plus a transform runner and a transform-aware hook.</summary>
sealed class TransformCoordinatorRig : IDisposable
{
    public readonly FakeTransformHotkeys Hotkeys = new();
    public readonly RecordingPresenter Ui = new();
    public readonly RemySettings Settings = new();
    public readonly TransformRig Rig = new();
    public readonly DictationCoordinator Coordinator;
    readonly string _dir = Path.Combine(Path.GetTempPath(), "remy-tr-" + Guid.NewGuid().ToString("N"));

    public TransformCoordinatorRig()
    {
        Coordinator = new DictationCoordinator(new DictationDependencies
        {
            Settings = Settings, Hotkeys = Hotkeys, Audio = new FakeAudio(), SystemAudio = new FakeSystemAudio(),
            Destinations = new FakeDestinations(), Clipboard = new FakeClipboard(), Sounds = new FakeSounds(),
            Recognizer = new FakeRecognizer(), Cleanup = new FakeCleanup(), History = () => new FakeHistory(),
            Recovery = new RecoveryStore(_dir), Models = new FakeModels(), Credentials = new FakeCredentials(), Presenter = Ui,
            Transforms = Rig.Runner,
        });
    }

    public void Dispose()
    {
        Coordinator.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }
}
