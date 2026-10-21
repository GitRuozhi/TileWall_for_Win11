using TileWall.Core.Application;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Application;

public sealed class MenuGroupExpansionTests
{
    [Fact]
    public void NewGroupsStartCollapsedAndSixthExpansionClosesOldest()
    {
        var state = new MenuGroupExpansion();
        Assert.Empty(state.Expanded);
        foreach (var id in new[] { "a", "b", "c", "d", "e", "f" }) state.Toggle(id);
        Assert.Equal(new[] { "b", "c", "d", "e", "f" }, state.Expanded);
        state.Toggle("c"); state.Toggle("c"); state.Toggle("g");
        Assert.Equal(new[] { "d", "e", "f", "c", "g" }, state.Expanded);
    }

    [Fact]
    public void PersistenceKeepsExpansionOrderAndStableIdsAcrossRenameOrMove()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "menu-state"));
        var store = new MenuStartStateStore(new InMemoryFileStore(), root);
        var state = store.Read(); state.Toggle("group-a"); state.Toggle("group-b");
        store.Write(state.Expanded);
        state = store.Read();
        Assert.Equal(new[] { "group-a", "group-b" }, state.Expanded);
        Assert.False(state.Retain(new[] { "group-b", "group-a", "new-group" }));
        Assert.True(state.Retain(new[] { "group-b" }));
        Assert.Equal(new[] { "group-b" }, state.Expanded);
    }
}
