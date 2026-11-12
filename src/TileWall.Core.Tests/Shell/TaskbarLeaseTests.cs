// Adapted from ReStart (MIT), commits 297c8f5 / b9c2718.
// Copyright (c) 2026 ReStart contributors. See TileWall/Licenses/ReStartLicense.txt.
using TileWall.ApplicationModel;
using TileWall.Shell.Interop;
using Xunit;
using System.ComponentModel;

namespace TileWall.Core.Tests.Shell;

public sealed class TaskbarLeaseTests
{
    private sealed class Guard(Action release) : IDisposable { public void Dispose() => release(); }
    private sealed class Store : ITaskbarRecoveryStore
    {
        internal TaskbarLeaseState? State;
        internal bool FailDelete;
        public IDisposable Acquire() => new Guard(() => { });
        public TaskbarLeaseState? Read() => State;
        public void Save(TaskbarLeaseState state)
        {
            if (State != null) throw new IOException("Existing owned transaction");
            State = state;
        }
        public void Update(TaskbarLeaseState state) { Assert.Equal(state.Token, State?.Token); State = state; }
        public void Delete(string token)
        {
            if (FailDelete) throw new IOException("Owned cleanup failure");
            Assert.Equal(token, State?.Token); State = null;
        }
    }
    private sealed class Tray
    {
        internal readonly List<(uint Message, nint Parameter)> Messages = [];
        internal bool Activated, TimerHeld, MenuOpen;
        internal Action<uint, nint>? Sending;

        internal void Send(nint window, uint message, nint parameter, nint data)
        {
            Messages.Add((message, parameter));
            Sending?.Invoke(message, parameter);
            Deliver(message, parameter);
        }

        internal void Deliver(uint message, nint parameter)
        {
            if (message == 0x0006) Activated = parameter != 0;
            if (message == 0x0211) TimerHeld = true;
            if (message == 0x0212) TimerHeld = false;
        }
    }

    private sealed class TimerBackend(Tray tray) : ITaskbarLeaseBackend
    {
        private readonly TaskbarActivationBackend _native = new(sendMessage: tray.Send, menuActive: _ => tray.MenuOpen) { LauncherWindow = 3 };
        public TaskbarLeaseState Capture(TaskbarLeaseMode mode) => new(2, Guid.NewGuid().ToString("N"), 1, 1, mode,
            [new(1, 2, 1, true, true, new(0, 0, 1280, 800), new(0, 798, 1280, 48), new(0, 752, 1280, 48), HoldAutoHide: true)]);
        public bool IsCurrent(TaskbarLeaseState state) => true;
        public void Apply(TaskbarLeaseState state, Action armRecovery) => _native.HoldTimer(1, reveal: true, armRecovery);
        public void Restore(TaskbarLeaseState state) => _native.ReleaseTimer(1, interacting: false, foreground: 3);
    }

    [Theory]
    [InlineData(0)] // Watchdog startup fails.
    [InlineData(1)] // Launcher request is cancelled.
    [InlineData(2)] // Watchdog fails its final readiness check.
    public void FailedPreparationDoesNotSendMessagesEvenIfAnOrphanedRecordRemains(int failure)
    {
        var store = new Store { FailDelete = true };
        var tray = new Tray();
        using var lease = new TaskbarLease(store, new TimerBackend(tray), _ => failure == 0
            ? throw new IOException("Owned watchdog startup failure") : new Guard(() => { }), () => failure != 2);
        Assert.False(lease.Synchronize(TaskbarLeaseMode.Reveal, () => failure != 1));
        Assert.Equal(TaskbarRecoveryPhase.Prepared, store.State!.Phase);
        Assert.Empty(tray.Messages);

        store.FailDelete = false;
        Assert.True(TaskbarRecovery.Run(store, new TimerBackend(tray), (_, _) => false));
        Assert.Null(store.State);
        Assert.Empty(tray.Messages);
    }

    [Theory]
    [InlineData(0x0006, false)]
    [InlineData(0x0006, true)]
    [InlineData(0x0211, false)]
    [InlineData(0x0211, true)]
    public void AcquisitionTimeoutRecoversBeforeTheNextMonitoringApply(int timedOutMessage, bool delivered)
    {
        var store = new Store();
        var tray = new Tray();
        using var lease = new TaskbarLease(store, new TimerBackend(tray), _ => new Guard(() => { }));
        tray.Sending = (message, parameter) =>
        {
            Assert.Equal(TaskbarRecoveryPhase.RestorePending, store.State!.Phase);
            if (message != timedOutMessage) return;
            if (delivered) tray.Deliver(message, parameter);
            tray.MenuOpen = true;
            tray.Sending = null;
            throw new Win32Exception(1460);
        };
        Assert.False(lease.Synchronize(TaskbarLeaseMode.Reveal));
        Assert.Equal(timedOutMessage == 0x0211 || delivered, tray.Activated);
        Assert.Equal(delivered && timedOutMessage == 0x0211, tray.TimerHeld);
        Assert.Equal(TaskbarRecoveryPhase.RestorePending, store.State!.Phase);
        var attempts = tray.Messages.Count;
        Assert.False(lease.Synchronize(TaskbarLeaseMode.Reveal));
        Assert.Equal(attempts, tray.Messages.Count);

        tray.MenuOpen = false;
        Assert.True(lease.Synchronize(TaskbarLeaseMode.Reveal));
        Assert.Equal(new (uint, nint)[] { (0x0006, 0), (0x0212, 1), (0x0006, 0x10001), (0x0211, 1) }, tray.Messages.Skip(attempts));
        Assert.True(lease.Synchronize(null));
        Assert.False(tray.Activated);
        Assert.False(tray.TimerHeld);
        Assert.Null(store.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PersistentRevealTimeoutStopsAfterOneRetry(bool closeCoalesced)
    {
        var store = new Store();
        var tray = new Tray();
        using var lease = new TaskbarLease(store, new TimerBackend(tray), _ => new Guard(() => { }));
        tray.Sending = (message, parameter) =>
        {
            if (message != 0x0211) return;
            tray.Deliver(message, parameter);
            throw new Win32Exception(1460);
        };

        for (var tick = 0; tick < 5; tick++)
        {
            Assert.False(lease.Synchronize(TaskbarLeaseMode.Reveal, revealRequestId: 1));
            Assert.Null(store.State);
            Assert.False(tray.TimerHeld);
        }
        Assert.Equal(2, tray.Messages.Count(message => message.Message == 0x0211));
        Assert.Equal(2, tray.Messages.Count(message => message.Message == 0x0212));

        if (!closeCoalesced) Assert.True(lease.Synchronize(null, revealRequestId: 1));
        tray.Sending = null;
        Assert.True(lease.Synchronize(TaskbarLeaseMode.Reveal, revealRequestId: closeCoalesced ? 2 : 1));
        Assert.True(tray.TimerHeld);
    }

    [Fact]
    public void FreshWatchdogRetriesDeletionWithoutSendingAnotherRelease()
    {
        var store = new Store();
        var tray = new Tray();
        using var lease = new TaskbarLease(store, new TimerBackend(tray), _ => new Guard(() => { }));
        Assert.True(lease.Synchronize(TaskbarLeaseMode.Reveal));
        store.FailDelete = true;
        Assert.False(lease.Synchronize(null));
        Assert.False(tray.TimerHeld);
        Assert.Equal(TaskbarRecoveryPhase.Restored, store.State!.Phase);

        var messages = tray.Messages.ToArray();
        tray.MenuOpen = true;
        Assert.False(TaskbarRecovery.Run(store, new TimerBackend(tray), (_, _) => false));
        store.FailDelete = false;
        Assert.True(TaskbarRecovery.Run(store, new TimerBackend(tray), (_, _) => false));
        Assert.Null(store.State);
        Assert.Equal(messages, tray.Messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshWatchdogDefersAnUncertainReleaseWhileExplorerMenuIsOpen(bool delivered)
    {
        var store = new Store();
        var tray = new Tray();
        using var lease = new TaskbarLease(store, new TimerBackend(tray), _ => new Guard(() => { }));
        Assert.True(lease.Synchronize(TaskbarLeaseMode.Reveal));
        tray.Sending = (message, _) =>
        {
            if (message != 0x0212) return;
            if (delivered) tray.TimerHeld = false;
            tray.Sending = null;
            throw new Win32Exception(1460);
        };
        Assert.False(lease.Synchronize(null));
        Assert.Equal(!delivered, tray.TimerHeld);
        Assert.Equal(TaskbarRecoveryPhase.RestorePending, store.State!.Phase);

        var messages = tray.Messages.ToArray();
        tray.MenuOpen = true;
        Assert.False(TaskbarRecovery.Run(store, new TimerBackend(tray), (_, _) => false));
        Assert.False(TaskbarRecovery.Run(store, new TimerBackend(tray), (_, _) => false));
        Assert.Equal(messages, tray.Messages);
        tray.MenuOpen = false;
        Assert.True(TaskbarRecovery.Run(store, new TimerBackend(tray), (_, _) => false));
        Assert.False(tray.TimerHeld);
        Assert.Null(store.State);
    }
}
