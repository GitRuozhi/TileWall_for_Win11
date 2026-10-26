using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Resources;

namespace TileWall.Views;

internal static class RecycleBinPresentation
{
    internal static string Title(WallMember member) => MemberPresentationRules.ResolveTitle(member) is { Length: > 0 } title
        ? title : ProductStrings.Get("Recycle.Untitled");
}
internal sealed class RecycleBinConfirmationDialog : IWallDialogSession
{
    private readonly ContentDialog _dialog;
    internal RecycleBinConfirmationDialog(Window owner, string title, string text)
    {
        _dialog = new ContentDialog { XamlRoot = (owner.Content as FrameworkElement)?.XamlRoot, Title = title,
            Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = text, Width = 500, TextWrapping = TextWrapping.Wrap } },
            PrimaryButtonText = ProductStrings.Get("Recycle.Continue"), CloseButtonText = ProductStrings.Get("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close };
        ActionIcon.DecorateDialog(_dialog, ActionIcon.Confirm, ActionIcon.Delete, ActionIcon.Cancel);
    }
    internal async Task<bool> ShowAsync() => await OwnedDialogWindow.ShowAsync(_dialog) == ContentDialogResult.Primary;
    public bool HasUnsavedDraft => false;
    public string? TrySaveNow() => null;
    public void DiscardDraft() { }
    public Task<ExitDecision> ConfirmExitAsync() => Task.FromResult(ExitDecision.DiscardAndExit);
    public Task WaitForPendingAsync() => Task.CompletedTask;
    public void Close() => _dialog.Hide();
    public void RequestDismiss() => Close();
    public void RefreshStrings() { }
}
