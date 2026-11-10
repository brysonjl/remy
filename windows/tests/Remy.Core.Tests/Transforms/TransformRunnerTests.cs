using Remy.Core.Cleanup;
using Remy.Core.Session;
using Remy.Core.Tests.Session;
using Remy.Core.Transforms;
using Xunit;

namespace Remy.Core.Tests.Transforms;

public class TransformRunnerTests
{
    static TransformOutcome Finished(TransformRig r) => Assert.IsType<TransformEvent.Finished>(r.Events[^1]).Outcome;

    [Fact]
    public async Task SuccessInsertsFinalizedOutputWithOneProviderRequest()
    {
        var r = new TransformRig();
        var outcome = await r.Runner.RunAsync(TransformRig.Polish, false);
        Assert.Equal(TransformOutcome.Inserted, outcome);
        Assert.Equal(1, r.Requests);
        Assert.Equal("<selected_text>rough text</selected_text>", r.Sent[0].User);
        Assert.Equal([("  Clearer text.\n", true)], r.Delivered);
        Assert.Equal(2, r.Events.Count);
        Assert.Equal(new TransformEvent.Processing("Polish", r.Target), r.Events[0]);
        Assert.Equal(TransformOutcome.Inserted, Finished(r));
        Assert.Equal(TransformPhase.Idle, r.Runner.Phase);
    }

    [Fact]
    public async Task NoSelectionIsANoOp()
    {
        var r = new TransformRig { ReadOverride = TransformReadResult.Reject(TransformNotice.NoSelection) };
        var outcome = await r.Runner.RunAsync(TransformRig.Polish, false);
        Assert.Equal(TransformOutcome.NotStarted(TransformNotice.NoSelection), outcome);
        Assert.Equal(0, r.Requests);
        Assert.Equal(0, r.Deliveries);
    }

    [Fact]
    public async Task WhitespaceAndOversizedSelectionsNeverReachTheProvider()
    {
        var r = new TransformRig { SelectionText = " \n\t" };
        Assert.Equal(TransformOutcome.NotStarted(TransformNotice.NoSelection), await r.Runner.RunAsync(TransformRig.Polish, false));
        r.SelectionText = string.Concat(Enumerable.Repeat("word ", 3001));
        Assert.Equal(TransformOutcome.NotStarted(TransformNotice.SelectionTooLong(3001)), await r.Runner.RunAsync(TransformRig.Polish, false));
        Assert.Equal(0, r.Requests);
    }

    [Fact]
    public async Task ProtectedFieldAndUnavailableProviderStopBeforeReading()
    {
        var r = new TransformRig { ReadOverride = TransformReadResult.Reject(TransformNotice.ProtectedField) };
        Assert.Equal(TransformOutcome.NotStarted(TransformNotice.ProtectedField), await r.Runner.RunAsync(TransformRig.Polish, false));
        Assert.Equal(1, r.Reads);

        var good = r.RouteResult;
        r.RouteResult = TransformRouteResult.Unavailable(TransformNotice.CleanupOff);
        Assert.Equal(TransformOutcome.NotStarted(TransformNotice.CleanupOff), await r.Runner.RunAsync(TransformRig.Polish, false));
        Assert.Equal(1, r.Reads);

        r.RouteResult = good;
        Assert.Equal(TransformOutcome.NotStarted(TransformNotice.EmptyPrompt), await r.Runner.RunAsync(new TransformInstruction("X", " \n"), false));
        Assert.Equal(0, r.Deliveries);
    }

    [Fact]
    public async Task ProviderErrorEmptyAndRefusalNeverPaste()
    {
        var r = new TransformRig { Provider = (_, _) => throw new CleanupFailureException(RemyCleanupFailure.Timeout) };
        Assert.Equal(TransformOutcome.Failed(TransformNotice.ProviderFailed(RemyCleanupFailure.Timeout)), await r.Runner.RunAsync(TransformRig.Polish, false));
        r.Provider = (_, _) => Task.FromResult("  ");
        Assert.Equal(TransformOutcome.Failed(TransformNotice.EmptyResult), await r.Runner.RunAsync(TransformRig.Polish, false));
        r.Provider = (_, _) => Task.FromResult("I'm sorry, but I can't help with that.");
        Assert.Equal(TransformOutcome.Failed(TransformNotice.Refusal), await r.Runner.RunAsync(TransformRig.Polish, false));
        Assert.Equal(0, r.Deliveries);
        Assert.Equal(TransformPhase.Idle, r.Runner.Phase);
    }

    [Fact]
    public async Task FocusMovedToAnotherAppCopiesWithoutPasting()
    {
        var r = new TransformRig { FocusUnchanged = false, DeliverResult = () => TransformDeliveryResult.CopyOnly };
        Assert.Equal(TransformOutcome.Copied(TransformNotice.FocusChanged), await r.Runner.RunAsync(TransformRig.Polish, false));
        Assert.False(r.Delivered[0].Paste);
    }

    [Fact]
    public async Task ChangedSelectionInSameAppCopiesWithoutPasting()
    {
        var r = new TransformRig { SelectionMatches = false, DeliverResult = () => TransformDeliveryResult.CopyOnly };
        Assert.Equal(TransformOutcome.Copied(TransformNotice.FocusChanged), await r.Runner.RunAsync(TransformRig.Polish, false));
        Assert.False(r.Delivered[0].Paste);
    }

    [Fact]
    public async Task DestinationRefusingPasteIsReportedAsCopied()
    {
        var r = new TransformRig { DeliverResult = () => TransformDeliveryResult.CopyOnly };
        Assert.Equal(TransformOutcome.Copied(null), await r.Runner.RunAsync(TransformRig.Polish, false));
        r.DeliverResult = () => TransformDeliveryResult.Failed;
        Assert.Equal(TransformOutcome.Failed(TransformNotice.DeliveryFailed), await r.Runner.RunAsync(TransformRig.Polish, false));
    }

    [Fact]
    public async Task CancelDuringProcessingNeverDelivers()
    {
        var gate = new TaskCompletionSource<string>();
        var r = new TransformRig { Provider = (_, _) => gate.Task };
        var run = r.Runner.RunAsync(TransformRig.Polish, false);
        await TransformRig.WaitFor(() => r.Runner.Phase == TransformPhase.Processing);
        Assert.True(r.Runner.CanCancel);
        Assert.True(r.Runner.Cancel());
        Assert.Equal(TransformPhase.Idle, r.Runner.Phase);
        gate.SetResult("Late.");
        Assert.Equal(TransformOutcome.Cancelled, await run);
        Assert.Equal(0, r.Deliveries);
        Assert.Equal(TransformOutcome.Cancelled, Finished(r));
    }

    [Fact]
    public async Task LateProviderResultAfterCancelCannotPaste()
    {
        var gate = new TaskCompletionSource<string>();
        var r = new TransformRig { Provider = (_, _) => gate.Task };   // ignores cancellation
        var run = r.Runner.RunAsync(TransformRig.Polish, false);
        await TransformRig.WaitFor(() => r.Runner.Phase == TransformPhase.Processing);
        r.Runner.Cancel();
        Assert.Equal(TransformOutcome.Cancelled, await run);
        gate.SetResult("Clearer text.");
        await Task.Delay(30);
        Assert.Equal(1, r.Requests);
        Assert.Equal(0, r.Deliveries);
        Assert.Equal(1, r.Events.Count(e => e is TransformEvent.Finished));
    }

    [Fact]
    public async Task MutualExclusionWithDictationAndConcurrentTransforms()
    {
        var gate = new TaskCompletionSource<string>();
        var r = new TransformRig { Provider = (_, _) => gate.Task };
        Assert.Equal(TransformOutcome.NotStarted(TransformNotice.DictationBusy), await r.Runner.RunAsync(TransformRig.Polish, true));
        Assert.Equal(0, r.Reads);

        var first = r.Runner.RunAsync(TransformRig.Polish, false);
        await TransformRig.WaitFor(() => r.Runner.Phase == TransformPhase.Processing);
        Assert.Equal(TransformOutcome.NotStarted(TransformNotice.AlreadyRunning), await r.Runner.RunAsync(TransformRig.Polish, false));
        Assert.True(r.Runner.IsBusy);

        gate.SetResult("Clearer text.");
        Assert.Equal(TransformOutcome.Inserted, await first);
        Assert.False(r.Runner.IsBusy);
    }

    [Fact]
    public async Task BusyRunnerRefusesDictationStart()
    {
        using var c = new TransformCoordinatorRig();
        var gate = new TaskCompletionSource<string>();
        c.Rig.Provider = (_, _) => gate.Task;
        var run = c.Rig.Runner.RunAsync(TransformRig.Polish, false);
        await TransformRig.WaitFor(() => c.Rig.Runner.Phase == TransformPhase.Processing);
        await c.Coordinator.StartFromMenuAsync();
        Assert.Equal(Remy.Core.Contracts.DictationStatus.Idle, c.Coordinator.Status);
        Assert.True(c.Ui.Has("busy:Already transforming"));
        gate.SetResult("Clearer text.");
        await run;
    }

    [Fact]
    public async Task CoordinatorRoutesCancelAvailabilityToTheRunner()
    {
        using var c = new TransformCoordinatorRig();
        c.Coordinator.Initialize();
        Assert.False(c.Hotkeys.CancellationEnabled);
        var gate = new TaskCompletionSource<string>();
        c.Rig.Provider = (_, _) => gate.Task;
        var run = c.Rig.Runner.RunAsync(TransformRig.Polish, false);
        await TransformRig.WaitFor(() => c.Hotkeys.CancellationEnabled);
        c.Hotkeys.RaiseCancel();
        await TransformRig.WaitFor(() => !c.Hotkeys.CancellationEnabled);
        Assert.Equal(TransformOutcome.Cancelled, await run);
    }

    [Fact]
    public async Task ShortcutPressRunsTheTransformAndDisabledOnesAreIgnored()
    {
        using var c = new TransformCoordinatorRig();
        var store = new TransformStore(c.Settings);
        var polish = BuiltInTransform.Polish.Id();
        store.SetShortcut(polish, 0x31, Remy.Core.Hotkeys.HotkeyModifiers.Ctrl | Remy.Core.Hotkeys.HotkeyModifiers.Alt);
        c.Coordinator.Initialize();
        Assert.Equal(polish, Assert.Single(c.Hotkeys.TransformConfigs[^1]).Id);

        store.SetEnabled(false, polish);
        c.Hotkeys.PressTransform(polish);
        await Task.Delay(50);
        Assert.Equal(0, c.Rig.Reads);

        store.SetEnabled(true, polish);
        c.Hotkeys.PressTransform(polish);
        await TransformRig.WaitFor(() => c.Rig.Deliveries == 1);
    }
}
