#nullable enable

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using YMusic.WinUI.Services.Interfaces;

namespace YMusic.WinUI.Services.Implementations;

public sealed class DialogService : IDialogService
{
    private static readonly SemaphoreSlim _dialogLock = new(1, 1);

    public async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (dialog == null) return ContentDialogResult.None;

        if (!await _dialogLock.WaitAsync(0).ConfigureAwait(false))
        {
            Debug.WriteLine("[DialogService] Another ContentDialog is already open. Suppressing concurrent dialog.");
            return ContentDialogResult.None;
        }

        try
        {
            if (dialog.XamlRoot == null && App.MainWindow?.Content?.XamlRoot != null)
            {
                dialog.XamlRoot = App.MainWindow.Content.XamlRoot;
            }

            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DialogService] Exception showing dialog: {ex.Message}");
            return ContentDialogResult.None;
        }
        finally
        {
            try
            {
                _dialogLock.Release();
            }
            catch { }
        }
    }
}
