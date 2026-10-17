using TileWall.Core.Shell;
using Microsoft.UI.Xaml.Controls;
using TileWall.Resources;

namespace TileWall.Views;

/// <summary>The active modal's small participation surface for the one application host.</summary>
internal interface IWallDialogSession : IExitParticipant
{
    Task<ExitDecision> ConfirmExitAsync();
    Task WaitForPendingAsync();
    void Close();
    void RequestDismiss();
    void RefreshStrings();
}

/// <summary>Temporarily replaces an existing dialog page, preserving even an unconfirmed layout page.</summary>
internal sealed class DialogExitPrompt(ContentDialog dialog)
{
    private TaskCompletionSource<ExitDecision>? _completion;
    private object? _content, _title;
    private string _primary = string.Empty, _secondary = string.Empty, _close = string.Empty;
    private ContentDialogButton _default;
    private (string Primary, string Secondary, string Close) _icons;
    public bool IsActive => _completion is not null;

    public Task<ExitDecision> ConfirmAsync()
    {
        if (_completion is not null) return _completion.Task;
        _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _content = dialog.Content; _title = dialog.Title;
        _primary = dialog.PrimaryButtonText; _secondary = dialog.SecondaryButtonText; _close = dialog.CloseButtonText;
        _default = dialog.DefaultButton;
        _icons = ActionIcon.GetDialogIcons(dialog);
        dialog.Title = ProductStrings.Get("Common.ExitTitle");
        dialog.Content = new TextBlock { Text = ProductStrings.Get("Common.ExitDraft"),
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, MaxWidth = 400 };
        dialog.PrimaryButtonText = ProductStrings.Get("Common.SaveAndExit");
        dialog.SecondaryButtonText = ProductStrings.Get("Common.DiscardAndExit");
        dialog.CloseButtonText = ProductStrings.Get("Common.CancelExit");
        dialog.DefaultButton = ContentDialogButton.None;
        ActionIcon.DecorateDialog(dialog, ActionIcon.Save, ActionIcon.Delete, ActionIcon.Previous);
        return _completion.Task;
    }

    public void Resolve(ExitDecision decision)
    {
        if (_completion is not { } completion) return;
        _completion = null;
        dialog.Content = _content; dialog.Title = _title;
        dialog.PrimaryButtonText = _primary; dialog.SecondaryButtonText = _secondary; dialog.CloseButtonText = _close;
        dialog.DefaultButton = _default;
        ActionIcon.DecorateDialog(dialog, _icons.Primary, _icons.Secondary, _icons.Close);
        completion.TrySetResult(decision);
    }
}
