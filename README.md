# Cross-platform Miracast receiver

A minimal Avalonia receiver with two platform backends:

- Windows 10 1903 or newer: `Windows.Media.Miracast` receives the connection and the Windows media frame server copies video into an Avalonia `WriteableBitmap`.
- Linux: the receiver drives Wi-Fi Direct through the system `wpa_supplicant` (D-Bus) and NetworkManager, implements the WFD/RTSP negotiation itself, and decodes the stream with `ffmpeg`.

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

## Linux requirements

- NetworkManager with `nmcli`, and the system `wpa_supplicant` it controls, built with D-Bus and P2P support.
- A Wi-Fi adapter/driver with P2P support. To keep the normal Wi-Fi connection while casting, the driver must support concurrent station and P2P operation.
- `ffmpeg` with the H.264 and AAC decoders and PulseAudio output (`ffmpeg -decoders`, `ffmpeg -devices`).
- `iproute2` (`ip`).

### One-time setup

The application never runs `sudo`, `su`, `pkexec` or a shell, and refuses to start as root, so two system settings have to be made once by an administrator.

1. Allow the desktop user to use `wpa_supplicant`'s D-Bus API. Fedora-family distributions only allow root by default, which fails with "not authorized". Replace `alice` with the user name:

   ```bash
   sudo tee /etc/dbus-1/system.d/wpa_supplicant-miracast.conf >/dev/null <<'XML'
   <!DOCTYPE busconfig PUBLIC "-//freedesktop//DTD D-BUS Bus Configuration 1.0//EN"
    "http://www.freedesktop.org/standards/dbus/1.0/busconfig.dtd">
   <busconfig>
     <policy user="alice">
       <allow send_destination="fi.w1.wpa_supplicant1"/>
       <allow receive_sender="fi.w1.wpa_supplicant1" receive_type="signal"/>
     </policy>
   </busconfig>
   XML
   sudo systemctl reload dbus-broker   # or dbus, depending on the distribution
   ```

   This grants that user full control of `wpa_supplicant`, not just P2P.

2. Allow the RTP stream (UDP `7236`) from the source. When this PC is the P2P group owner, NetworkManager runs a DHCP server on the group interface and, with firewalld, places it in the `nm-shared` zone, which rejects everything except DHCP, DNS and SSH:

   ```bash
   sudo firewall-cmd --permanent --zone=nm-shared --add-port=7236/udp
   sudo firewall-cmd --reload
   ```

   With another firewall, allow inbound UDP `7236` on `p2p-*` interfaces. No inbound TCP port is needed: the receiver opens the RTSP connection to the source.

### How it works

- The receiver sets the Wi-Fi Display sink information elements and push-button WPS config methods in `wpa_supplicant`, then listens and runs P2P discovery.
- When a source asks to connect, the receiver activates an in-memory NetworkManager `wifi-p2p` connection to it. NetworkManager removes any P2P group it did not start itself, so the group has to come from NetworkManager; it also handles IPv4 for both group roles.
- Once the group is up, the receiver connects to the source's RTSP port (`7236`), negotiates the session and asks the source for a fresh IDR frame.
- One `ffmpeg` process receives the MPEG-TS/RTP stream on UDP `7236`, decodes it with low latency, plays the audio through PulseAudio/PipeWire and writes BGRA frames that pass through a two-frame jitter buffer into the same Avalonia `WriteableBitmap` path used on Windows.
- The physical Wi-Fi adapter stays connected and managed by NetworkManager throughout.

On shutdown the receiver deletes its NetworkManager connection, stops discovery and extended listen, and restores the previous WFD information elements and WPS config methods. NetworkManager clears the WFD information elements when a P2P connection ends, so the receiver applies them again after each session.

### Configuration

- `MIRACAST_WIFI_INTERFACE` selects the Wi-Fi adapter. Without it, the receiver prefers a disconnected Wi-Fi adapter and otherwise uses the first one NetworkManager reports.
- `MIRACAST_FRIENDLY_NAME` overrides the advertised sink name; the default is `<hostname> Miracast`.

### Limitations

- The source picks the video format from what the receiver offers (up to 1080p60). Many phones cap Miracast in their vendor configuration; for example, a Qualcomm-based Android phone tested here only sends 1280x720 at 30 fps.
- Only push-button pairing is supported, and audio is negotiated as AAC 48 kHz stereo.
- If an Android source keeps failing to connect after an earlier session, remove this receiver from its remembered Wi-Fi Direct groups.
