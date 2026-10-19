using TileWall.Core.Application;
using TileWall.Core.Shell;
using TileWall.Core.Tests.Shell;
using Xunit;

namespace TileWall.Core.Tests.Application;

public sealed class ExitDraftSavePermissionTests
{
    private sealed class Participant(Func<string?> save) : IExitParticipant
    {
        public bool HasUnsavedDraft => true;
        public string? TrySaveNow() => save();
        public void DiscardDraft() { }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("save failed", false)]
    public void ExistingExitSaveCanWriteWithoutOpeningAdmissionAndPermissionAlwaysEnds(string? error, bool succeeds)
    {
        var permission = new ExitDraftSavePermission();
        var host = new FakeExitHost();
        var exit = new ExitCoordinator(host);
        var other = new FakeParticipant();
        IExitParticipant? participant = null;
        participant = new Participant(() =>
        {
            Assert.False(permission.BlocksMutation(pending: false, admissionClosed: true, participant));
            Assert.True(permission.BlocksMutation(pending: true, admissionClosed: true, participant));
            Assert.True(permission.BlocksMutation(pending: false, admissionClosed: true, other));
            Assert.True(permission.BlocksMutation(pending: false, admissionClosed: true, null));
            return error;
        });
        host.TopParticipant = participant;
        Assert.True(permission.BlocksMutation(false, true, participant));
        var result = permission.Run(participant, () => host.TopParticipant,
            () => exit.ApplyDecision(participant, ExitDecision.SaveAndExit));
        Assert.Equal(succeeds, result);
        Assert.Equal(succeeds, host.CloseTopSessionCalled);
        Assert.True(permission.BlocksMutation(false, true, participant));
    }

    [Fact]
    public void ReplacementParticipantCannotReceiveOldSavePermission()
    {
        var permission = new ExitDraftSavePermission();
        var old = new FakeParticipant();
        var replacement = new FakeParticipant();
        var invoked = false;
        Assert.Throws<InvalidOperationException>(() => permission.Run(old, () => replacement,
            () => { invoked = true; return true; }));
        Assert.False(invoked);
        Assert.False(permission.Allows(old));
        Assert.False(permission.Allows(replacement));
    }

    [Fact]
    public void ThrowingSaveRevokesPermissionAndReentrantSaveCannotBroadenIt()
    {
        var permission = new ExitDraftSavePermission();
        var participant = new FakeParticipant();
        Assert.Throws<IOException>(() => permission.Run<bool>(participant, () => participant, () =>
        {
            Assert.True(permission.Allows(participant));
            Assert.Throws<InvalidOperationException>(() => permission.Run(participant, () => participant, () => true));
            throw new IOException("save failed");
        }));
        Assert.True(permission.BlocksMutation(false, true, participant));
    }
}
