using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Miracast.Avalonia.ViewModels;
using Miracast.Receiver;
using Miracast.Receiver.Entities.EventArgs;

namespace Miracast.Avalonia.Views;

public partial class MainWindow : Window
{
    private readonly IMiracastReceiverService _receiver;
    private readonly IVideoRenderer _videoRenderer;
    private readonly CancellationTokenSource _lifetime = new();
    private WriteableBitmap? _bitmap;
    private bool _shutdownInProgress;
    private bool _allowClose;

    public MainWindow(
        IMiracastReceiverService receiver,
        IVideoRenderer videoRenderer,
        MainWindowViewModel viewModel)
    {
        _receiver = receiver;
        _videoRenderer = videoRenderer;

        InitializeComponent();
        DataContext = viewModel;

        _videoRenderer.FrameReceived += OnFrameReceived;
        _receiver.ConnectionCreated += OnConnectionCreated;
        _receiver.ConnectionClosed += OnConnectionClosed;
        _receiver.VideoReceived += OnVideoReceived;
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    private async void OnOpened(object? sender, EventArgs args)
    {
        try
        {
            await _receiver.StartAsync(_lifetime.Token);
            SetStatus("Ready — choose this PC in the source device's Cast menu.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"Receiver failed to start: {exception.Message}");
        }
    }

    private void OnConnectionCreated(object? sender, ConnectionCreatedEventArgs args) =>
        SetStatus(args.DeviceName is null ? "Source connected." : $"Connected: {args.DeviceName}");

    private async void OnConnectionClosed(object? sender, ConnectionClosedEventArgs args)
    {
        await StopPlaybackAsync();
        SetStatus("Source disconnected. Waiting for a connection…");
    }

    private async void OnVideoReceived(object? sender, VideoReceivedEventArgs args)
    {
        try
        {
            await _videoRenderer.PlayAsync(args.Source, _lifetime.Token);
            SetStatus("Streaming", autoHide: true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"Could not play the incoming stream: {exception.Message}");
        }
    }

    private void OnFrameReceived(object? sender, VideoFrameReceivedEventArgs frame)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_bitmap is null
                    || _bitmap.PixelSize.Width != frame.Width
                    || _bitmap.PixelSize.Height != frame.Height)
                {
                    _bitmap?.Dispose();
                    _bitmap = new WriteableBitmap(
                        new PixelSize(frame.Width, frame.Height),
                        new Vector(96, 96),
                        PixelFormat.Bgra8888,
                        AlphaFormat.Opaque);
                    FrameImage.Source = _bitmap;
                }

                using var target = _bitmap.Lock();
                var bytesPerRow = Math.Min(frame.RowBytes, target.RowBytes);
                for (var row = 0; row < frame.Height; row++)
                {
                    Marshal.Copy(
                        frame.Pixels,
                        row * frame.RowBytes,
                        IntPtr.Add(target.Address, row * target.RowBytes),
                        bytesPerRow);
                }

                FrameImage.InvalidateVisual();
            }
            finally
            {
                frame.Dispose();
            }
        }, DispatcherPriority.Render);
    }

    private Task StopPlaybackAsync() => _videoRenderer.StopAsync();

    private void SetStatus(string status, bool autoHide = false) =>
        Dispatcher.UIThread.Post(() =>
        {
            ViewModel.Status = status;
            StatusBar.IsVisible = true;
            if (autoHide)
            {
                DispatcherTimer.RunOnce(
                    () => StatusBar.IsVisible = ViewModel.Status != status,
                    TimeSpan.FromSeconds(5));
            }
        });

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_allowClose)
            return;

        args.Cancel = true;
        if (_shutdownInProgress)
            return;

        _shutdownInProgress = true;
        SetStatus("Stopping receiver and restoring networking…");
        _lifetime.Cancel();

        try
        {
            await StopPlaybackAsync();
            await _receiver.StopAsync();
        }
        catch
        {
            // Cleanup is best-effort; the window must still be able to close.
        }

        _videoRenderer.FrameReceived -= OnFrameReceived;
        _receiver.ConnectionCreated -= OnConnectionCreated;
        _receiver.ConnectionClosed -= OnConnectionClosed;
        _receiver.VideoReceived -= OnVideoReceived;
        _bitmap?.Dispose();
        _lifetime.Dispose();

        _allowClose = true;
        Close();
    }
}
