using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TileWall.Core.Shell;
using TileWall.Resources;

namespace TileWall.Views;

internal sealed record WindowsStartImportResult(string Message, bool Failed = false);

internal sealed class WindowsStartImportInvitation : IWallDialogSession
{
    private readonly ContentDialog _dialog;
    private readonly TextBlock _message = new() { MaxWidth = 420, TextWrapping = TextWrapping.Wrap };
    private readonly Func<CancellationToken, Task<WindowsStartImportResult>> _import;
    private CancellationTokenSource? _preparation;
    private Task? _pending;
    private WindowsStartImportResult? _result;
    private bool _dismissRequested;

    internal WindowsStartImportInvitation(Window owner, Func<CancellationToken, Task<WindowsStartImportResult>> import)
    {
        _import = import;
        _dialog = new ContentDialog
        {
            XamlRoot = (owner.Content as FrameworkElement)?.XamlRoot,
            Content = _message,
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(_dialog, "windows-start-import-invitation");
        ActionIcon.DecorateDialog(_dialog, ActionIcon.Import, ActionIcon.Cancel, ActionIcon.Cancel);
        RefreshStrings();
        _dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (_dismissRequested || _preparation is not null) { args.Cancel = true; return; }
            var deferral = args.GetDeferral();
            using var cancellation = new CancellationTokenSource();
            _preparation = cancellation;
            _dialog.IsPrimaryButtonEnabled = false;
            RefreshStrings();
            try { await (_pending = RunImportAsync(cancellation.Token)); }
            finally
            {
                _pending = null;
                _preparation = null;
                deferral.Complete();
            }
        };
        _dialog.Closing += (_, args) =>
        {
            if (_preparation is null) return;
            args.Cancel = true;
            RequestDismiss();
        };
    }

    internal async Task<WindowsStartImportResult?> ShowAsync()
    {
        await OwnedDialogWindow.ShowAsync(_dialog);
        return _result;
    }

    private async Task RunImportAsync(CancellationToken token)
    {
        try { _result = await _import(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public bool HasUnsavedDraft => false;
    public string? TrySaveNow() => null;
    public void DiscardDraft() { }
    public Task<ExitDecision> ConfirmExitAsync() => Task.FromResult(ExitDecision.DiscardAndExit);
    public Task WaitForPendingAsync()
    {
        _dismissRequested = true;
        _preparation?.Cancel();
        return _pending ?? Task.CompletedTask;
    }

    public void Close() => RequestDismiss();

    public void RequestDismiss()
    {
        _dismissRequested = true;
        if (_preparation is { } request) request.Cancel();
        else _dialog.Hide();
    }

    public void RefreshStrings()
    {
        _dialog.Title = ProductStrings.Get("WindowsStartImport.Setting");
        _dialog.PrimaryButtonText = ProductStrings.Get("WindowsStartImport.Import");
        _dialog.CloseButtonText = ProductStrings.Get(_preparation is null ? "WindowsStartImport.Later" : "Common.Cancel");
        _message.Text = ProductStrings.Get(_preparation is null ? "WindowsStartImport.Invitation" : "WindowsStartImport.Progress");
    }
}
