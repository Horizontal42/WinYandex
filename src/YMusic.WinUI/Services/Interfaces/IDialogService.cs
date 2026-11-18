#nullable enable

using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;

namespace YMusic.WinUI.Services.Interfaces;

public interface IDialogService
{
    Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog);
}
