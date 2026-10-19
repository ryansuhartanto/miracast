using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using Miracast.Receiver.Entities.EventArgs;

namespace Miracast.Receiver.Linux;

public sealed class VideoRenderer : IVideoRenderer, IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Process? _ffmpeg;
    private Task? _reader;
    private int _framePending;
    private double _frameIntervalMs;
    private bool _disposed;

    public event EventHandler<VideoFrameReceivedEventArgs>? FrameReceived;

    public async Task PlayAsync(IVideoSource source, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The ffmpeg video renderer can only run on Linux.");
        if (source is not VideoSource linuxSource)
            throw new ArgumentException("The source is not a WFD RTP source.", nameof(source));
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await StopCoreAsync().ConfigureAwait(false);

            var startInfo = new ProcessStartInfo("ffmpeg")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            foreach (var argument in BuildArguments(linuxSource))
                startInfo.ArgumentList.Add(argument);

            Process? ffmpeg;
            try
            {
                ffmpeg = Process.Start(startInfo);
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                throw new InvalidOperationException(
                    "Could not start 'ffmpeg'. Make sure it is installed and available in PATH.", exception);
            }

            _ffmpeg = ffmpeg ?? throw new InvalidOperationException("Could not start ffmpeg.");
            _reader = ReadFramesAsync(ffmpeg.StandardOutput.BaseStream, linuxSource.Width, linuxSource.Height);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    // Low-latency decode: no input buffering, no A/V clock, every decoded frame is
    // written out immediately as BGRA. Audio goes straight to PulseAudio/PipeWire.
    internal static IReadOnlyList<string> BuildArguments(VideoSource source) =>
    [
        "-hide_banner", "-loglevel", "warning", "-nostdin",
        "-fflags", "nobuffer", "-flags", "low_delay", "-err_detect", "ignore_err",
        "-probesize", "500000", "-analyzeduration", "500000", "-reorder_queue_size", "0",
        "-i", source.StreamUri.OriginalString,
        "-map", "0:v:0", "-fps_mode", "passthrough",
        "-vf", $"scale={source.Width}:{source.Height}", "-pix_fmt", "bgra",
        "-f", "rawvideo", "pipe:1",
        "-map", "0:a:0?", "-f", "pulse", "Miracast",
    ];

    // Wi-Fi delivers frames in bursts. Holding a couple and presenting them at the
    // measured frame interval keeps motion even for about two frames of latency.
    private const int PrebufferFrames = 2;

    private async Task ReadFramesAsync(Stream stream, int width, int height)
    {
        var length = width * 4 * height;
        _frameIntervalMs = 1000.0 / 30;
        var frames = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(PrebufferFrames + 2)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            },
            dropped => ArrayPool<byte>.Shared.Return(dropped));
        var presenter = PresentFramesAsync(frames.Reader, width, height);
        try
        {
            var lastArrival = Stopwatch.GetTimestamp();
            while (true)
            {
                var pixels = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    await stream.ReadExactlyAsync(pixels, 0, length).ConfigureAwait(false);
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(pixels);
                    throw;
                }

                var arrival = Stopwatch.GetTimestamp();
                var interval = Stopwatch.GetElapsedTime(lastArrival, arrival).TotalMilliseconds;
                lastArrival = arrival;
                _frameIntervalMs = Math.Clamp(_frameIntervalMs * 0.95 + interval * 0.05, 1000.0 / 60, 1000.0 / 24);
                frames.Writer.TryWrite(pixels);
            }
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            frames.Writer.Complete();
            await presenter.ConfigureAwait(false);
        }
    }

    private async Task PresentFramesAsync(ChannelReader<byte[]> frames, int width, int height)
    {
        var clock = Stopwatch.StartNew();
        var due = TimeSpan.Zero;
        var primed = false;
        while (await frames.WaitToReadAsync().ConfigureAwait(false))
        {
            if (!primed)
            {
                // ponytail: polls until the prebuffer fills, once per stream.
                while (frames.Count < PrebufferFrames && !frames.Completion.IsCompleted)
                    await Task.Delay(2).ConfigureAwait(false);
                primed = true;
                due = clock.Elapsed;
            }

            var wait = due - clock.Elapsed;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait).ConfigureAwait(false);
            if (!frames.TryRead(out var pixels))
                continue;

            Present(pixels, width, height);

            // Run slightly fast while frames pile up so the queue drains back to the
            // prebuffer size instead of adding latency; after an underrun, restart the
            // schedule from now rather than bursting to catch up.
            var interval = _frameIntervalMs * (frames.Count > PrebufferFrames ? 0.9 : 1.0);
            var now = clock.Elapsed;
            due = (due > now ? due : now) + TimeSpan.FromMilliseconds(interval);
        }
    }

    private void Present(byte[] pixels, int width, int height)
    {
        // Drop the frame while the window is still drawing the previous one.
        if (FrameReceived is not { } handler || Interlocked.CompareExchange(ref _framePending, 1, 0) != 0)
        {
            ArrayPool<byte>.Shared.Return(pixels);
            return;
        }

        var frame = new VideoFrameReceivedEventArgs(
            pixels,
            width,
            height,
            width * 4,
            () =>
            {
                ArrayPool<byte>.Shared.Return(pixels);
                Interlocked.Exchange(ref _framePending, 0);
            });
        try
        {
            handler(this, frame);
        }
        catch
        {
            frame.Dispose();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        var ffmpeg = _ffmpeg;
        _ffmpeg = null;
        if (ffmpeg is not null)
        {
            try
            {
                ffmpeg.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        if (_reader is not null)
            await _reader.ConfigureAwait(false);
        _reader = null;
        ffmpeg?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }
}
