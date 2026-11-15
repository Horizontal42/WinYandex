#nullable enable

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using YMusic.WinUI.ViewModels;

namespace YMusic.WinUI.Views.Controls;

public sealed partial class PlayerBarControl : UserControl
{
    public PlayerBarViewModel ViewModel { get; }

    public PlayerBarControl()
    {
        ViewModel = App.GetService<PlayerBarViewModel>();
        InitializeComponent();
        Bindings.Update();

        SeekSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(SeekSlider_PointerPressed), true);
        SeekSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(SeekSlider_PointerReleased), true);
        SeekSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(SeekSlider_PointerCaptureLost), true);

        VolumeContainer.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(VolumeContainer_PointerWheelChanged), true);
    }

    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        Bindings.Update();
    }

    private void VolumeContainer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
        if (delta != 0)
        {
            double step = delta > 0 ? 2.0 : -2.0;
            ViewModel.ChangeVolume(step);
            e.Handled = true;
        }
    }

    private void VolumePercentageBadge_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (VolumePercentageBox != null && VolumePercentageBadge != null)
        {
            VolumePercentageBadge.Visibility = Visibility.Collapsed;
            VolumePercentageBox.Visibility = Visibility.Visible;
            VolumePercentageBox.Text = Math.Round(ViewModel.Volume).ToString();
            VolumePercentageBox.Focus(FocusState.Programmatic);
            VolumePercentageBox.SelectAll();
            e.Handled = true;
        }
    }

    private void VolumePercentageBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitVolumeText();
    }

    private void VolumePercentageBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            CommitVolumeText();
            VolumeSlider.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (VolumePercentageBox != null && VolumePercentageBadge != null)
            {
                VolumePercentageBox.Visibility = Visibility.Collapsed;
                VolumePercentageBadge.Visibility = Visibility.Visible;
            }
            VolumeSlider.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private void CommitVolumeText()
    {
        if (VolumePercentageBox == null || VolumePercentageBadge == null) return;
        ViewModel.SetVolumeFromInput(VolumePercentageBox.Text);
        VolumePercentageBox.Visibility = Visibility.Collapsed;
        VolumePercentageBadge.Visibility = Visibility.Visible;
    }

    private void SeekSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ViewModel.StartSeeking();
    }

    private void SeekSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Slider slider)
        {
            ViewModel.FinishSeeking(slider.Value);
        }
    }

    private void SeekSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Slider slider)
        {
            ViewModel.FinishSeeking(slider.Value);
        }
    }

    private void CoverButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (CoverHoverOverlay != null)
        {
            CoverHoverOverlay.Opacity = 1;
        }
    }

    private void CoverButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (CoverHoverOverlay != null)
        {
            CoverHoverOverlay.Opacity = 0;
        }
    }

    private async void TrackOptionsFlyout_Opening(object? sender, object e)
    {
        await ViewModel.PopulatePlaylistSubItemAsync(AddToPlaylistSubItem);
    }

    private async void QueueListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is YMusic.WinUI.Models.Track track)
        {
            await ViewModel.PlayQueueTrackCommand.ExecuteAsync(track);
        }
    }

    private async void PlayedQueueListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is YMusic.WinUI.Models.Track track)
        {
            await ViewModel.PlayPlayedTrackCommand.ExecuteAsync(track);
        }
    }

    private int _draggedFromIndex = -1;

    private void UpNextQueueListView_DragItemsStarting(object sender, DragItemsStartingEventArgs args)
    {
        if (args.Items?.Count > 0 && args.Items[0] is YMusic.WinUI.Models.Track movedTrack)
        {
            _draggedFromIndex = ViewModel.UpNextTracks.IndexOf(movedTrack);
        }
        else
        {
            _draggedFromIndex = -1;
        }
    }

    private void UpNextQueueListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.Items?.Count > 0 && args.Items[0] is YMusic.WinUI.Models.Track movedTrack)
        {
            int newIdx = ViewModel.UpNextTracks.IndexOf(movedTrack);
            int oldIdx = _draggedFromIndex;
            _draggedFromIndex = -1;

            if (oldIdx < 0)
            {
                var player = App.GetService<YMusic.WinUI.Services.Interfaces.IAudioPlayerService>();
                var upcoming = player?.UpcomingQueue;
                if (upcoming != null)
                {
                    for (int u = 0; u < upcoming.Count; u++)
                    {
                        if (YMusic.WinUI.Models.Track.IsSameTrackId(upcoming[u].Id, movedTrack.Id))
                        {
                            oldIdx = u;
                            break;
                        }
                    }
                }
            }

            if (newIdx >= 0 && oldIdx >= 0 && oldIdx != newIdx)
            {
                ViewModel.ReorderUpcomingTrack(oldIdx, newIdx);
            }
        }
    }
}

