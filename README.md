# Cross-platform Miracast receiver

A minimal Avalonia receiver with two platform backends:

- Windows 10 1903 or newer: `Windows.Media.Miracast` receives the connection and the Windows media frame server copies video into an Avalonia `WriteableBitmap`.
- Linux: the receiver uses the system `wpa_supplicant` over D-Bus for Wi-Fi Direct and implements WFD/RTSP negotiation itself; `ffmpeg` receives the MPEG-TS/RTP stream on UDP port `7236`, decodes it with low latency and plays its audio through PulseAudio/PipeWire, and the video renders inside the Avalonia window.

## Build and run

.NET 8 SDK is required.

```text
dotnet restore CrossplatformMiracast.sln
dotnet build CrossplatformMiracast.sln
dotnet run --project Miracast.Avalonia/Miracast.Avalonia.csproj
```

The UI project and both backends target plain `net8.0`. At startup, dependency injection selects the Windows or Linux receiver and video renderer with `OperatingSystem.IsWindows()` / `OperatingSystem.IsLinux()`. There are no platform preprocessor directives and no platform build property to pass.

`Miracast.Worker` is the headless Miracast backend used by Multiwall. It exchanges receiver state, control commands, and BGRA video frames with the Widget over a duplex named pipe. Publish framework-dependent single-file executables with `dotnet publish Miracast.Worker/Miracast.Worker.csproj -c Release -r win-x64 --self-contained false` and the corresponding `linux-x64` command. Multiwall installs them under the appropriate `multiwall-applications-windows` / `multiwall-applications-linux` directory; `ScreenDemonstrationWindow` starts the worker as its child process when the window opens.

## Windows requirements

- Windows 10 version 1903 or newer.
- A Wi-Fi adapter and driver with Wi-Fi Direct/Miracast receiver support.
- No VLC installation is used on Windows.

## Linux requirements

The host must provide:

- `nmcli` (NetworkManager CLI);
- a system `wpa_supplicant` built with D-Bus and P2P support and controlled by NetworkManager;
- a Wi-Fi adapter/driver that supports P2P; concurrent infrastructure/P2P support is required when the same adapter must retain its normal network connection;
- `ffmpeg` with H.264 decoding and PulseAudio output; decoded BGRA frames are copied into the same Avalonia `WriteableBitmap` path used on Windows.

The desktop user must be allowed by the machine's D-Bus/polkit policy to use the `fi.w1.wpa_supplicant1` P2P API and configure the P2P group interface through NetworkManager. Open TCP and UDP port `7236` in the local firewall.

At runtime the application never invokes `sudo`, `su`, `pkexec`, or a shell, and it refuses to start as root. It talks to the existing system `wpa_supplicant`, advertises the Wi-Fi Display sink information elements, accepts PBC requests, and starts P2P discovery. The physical Wi-Fi adapter is never disconnected and is never switched to NetworkManager's unmanaged state. Only the virtual P2P group interface is handed to NetworkManager for IPv4 configuration.

Set `MIRACAST_WIFI_INTERFACE` to choose a specific adapter. Without it, the receiver prefers a disconnected Wi-Fi adapter and otherwise uses the first Wi-Fi adapter reported by NetworkManager. Set `MIRACAST_FRIENDLY_NAME` to override the advertised sink name; the default is `<hostname> Miracast`.

On shutdown, discovery and extended listen are stopped and the previous global WFD information elements are restored. NetworkManager remains in control of the physical adapter throughout the receiver lifetime.
