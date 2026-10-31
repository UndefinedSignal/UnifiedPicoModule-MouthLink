# Privacy and network security

English | [简体中文](privacy_zh.md) · [README](../README.md)

This document describes what data UnifiedPicoModule processes, which files and log entries it leaves on the local machine, which packets it sends and receives on the local network, and what to check before sharing logs or configuration. The descriptions below are based on the current source code.

## Data processed

The module receives the following tracking data from the sender. The exact content depends on the sender:

- gaze direction, eye openness and pupil diameter;
- facial expression weights.

This data is processed in memory only. The module decodes and maps it, applies optional post-processing, and writes the result to VRCFT's `UnifiedTracking` data structure. Any further forwarding, for example over OSC to VRChat, is handled by VRCFT and VRChat and is outside the scope of this module.

The module does not:

- write raw tracking frames, decoded values or packet captures to disk;
- access the internet. It contains no telemetry, automatic updates or online downloads, and the icon shown in the module list is embedded in the DLL as a resource;
- collect identifying information such as accounts or device serial numbers.

The identifier used for Bridge channel subscriptions is a random 16-digit hexadecimal string generated for each session. It is not derived from the device or the user.

## Local files

At runtime, the module writes a file in one case only: when `UnifiedPicoModule.json` does not exist in the module directory, it creates a default configuration. An existing configuration file is only read and never rewritten. If reading fails, the module uses the defaults and leaves the original file unchanged.

The configuration file may contain the `Bridge.IP`, `Streaming.IP` and listen addresses entered by the user, as well as the `Migration` metadata written during migration.

`UnifiedPicoConfigTool` writes only to paths given explicitly on the command line and never overwrites existing files. The new configuration and migration report produced by the `import` command may contain:

- the full path of the old configuration file. If the file is located under the user profile, the path includes the Windows user name;
- the output path of the new configuration;
- the import time (UTC);
- device IP addresses from the old configuration;
- unrecognized old fields and their raw values.

## Log content

The module writes messages through the logging interface provided by VRCFT and does not create log files of its own. Whether and where the log is saved to disk is determined by VRCFT.

The log does not contain gaze, openness, pupil or expression values, but it may contain:

- the full path of the configuration file. This path is under `%APPDATA%` and includes the Windows user name;
- unrecognized configuration keys with their raw values, invalid values, and the parameters of enabled post-processing rules;
- listen addresses and ports, and the IP address and port of a locked peer, for example `Bridge peer locked: 192.168.1.20:9030`;
- the destination address of a failed UDP send;
- a status line every 5 seconds, containing the connection state, session number, receive rates, data age, validity flags, the capabilities announced by the sender (enhanced mode, per-eye capability), the dropped packet count, the gaze origin, and whether the pupil value is measured or estimated;
- exception types and messages.

Each type of warning is written at most once every 5 seconds, and new messages are discarded while more than 32 messages from a data source are waiting to be written. As a result, a continuous stream of malformed packets does not flood the log.

## Network communication

The module uses the following UDP ports:

| Port | Purpose |
|---|---|
| 9030 | PicoFacialBridge and PicoFacialDataDaemon |
| 29765 | Streaming format with a 16-byte header, e.g. PICO Connect |
| 29763 | Headerless streaming format |

The default listen address is `0.0.0.0`, which covers all IPv4 interfaces on the machine. It can be restricted to a single interface with `Bridge.ListenAddress` and `Streaming.ListenAddress`. Ports are bound exclusively. When `Source` is `Bridge`, the streaming ports are not opened; when it is `Streaming`, port 9030 is not opened.

Besides receiving data, the module sends a small number of control packets on the local network. These packets contain no tracking data:

- Bridge discovery. When `Bridge.IP` is not set, the module sends `DISCOVER_DAEMON` to the multicast address `239.255.255.250:9030` on each active wired or wireless adapter. The multicast TTL is 1, so the packet is not forwarded by routers. Adapters whose names contain keywords such as VPN, virtual or tunnel are excluded. When `Bridge.IP` is set, the request is sent by unicast to that address instead;
- Bridge heartbeat and subscription. The module replies `POLO` to the locked peer, sends `PXR_SUB` channel subscription requests, and sends `STOP` when it shuts down;
- Streaming heartbeat. When the sender sends a `MARCO` heartbeat, the module replies `POLO`.

## Source validation

UDP has no encryption or authentication, and the source address of a packet can be forged. The module applies the following checks to reduce accidental reception. These checks cannot defend against deliberate forgery or tampering on the local network.

General checks:

- Only IPv4 unicast source addresses are accepted.
- Packet length is validated for each format; see the [protocol reference](protocol.md#overview) for the accepted lengths. Packets larger than 2048 bytes are discarded.
- When a connection is established, the first data packet must contain valid fields.
- Data with a duplicate or backward timestamp is discarded.
- Within a session, only the first protocol that passes validation is used.

Bridge (9030):

- The source port must be 9030.
- When `Bridge.IP` is set, only that address is accepted. Otherwise, only addresses on the same subnet as a discovery adapter are accepted, and the machine's own addresses are excluded.
- Before a peer is locked, data is accepted only within a time window after a discovery request was sent. The window length is `Bridge.ReconnectMs`, with a minimum of 1 second.
- Once a peer is locked, only data from the same IP address and port is accepted.
- Mode announcements must consist of printable ASCII characters and contain exactly the five defined fields. Unknown fields never enable enhanced capabilities.
- Subscription acknowledgements must match the random identifier of the current session, their channel mask must not exceed the requested channels, and their epoch must not go backward. BridgeSplit data must match the acknowledged epoch and channel mask.
- If the peer shows no activity within `Bridge.ControlTimeoutMs` (30 seconds by default), the module releases the lock and starts discovery again.

Streaming (29765 / 29763):

- `Streaming.LocalOnly` is `true` by default. In this mode, only data from loopback addresses or the machine's own adapter addresses is accepted. If `Streaming.IP` is not a local address under this setting, the streaming source fails to start.
- When `Streaming.IP` is set, only that address is accepted.
- After the first valid data frame arrives, the module locks the sender's IP address and port, as well as the protocol of that port. The lock is released when no data arrives within `Streaming.SessionTimeoutMs` (2 seconds by default).
- By default, the header format does not validate the magic, subtype, fragment and version fields of the header. These fields are checked individually only when `Streaming.HeaderProfile` is configured.

## Recommendations

- Use the module only on a trusted local network. On networks such as public Wi-Fi, other devices on the same subnet can send data to these ports.
- Do not configure port forwarding for 9030, 29765 or 29763 on the router, and do not expose these ports to the internet.
- In Windows Firewall, allow VRCFT to receive on these ports only under the Private network profile.
- When using streaming software that runs on the same PC, such as PICO Connect, keep `Streaming.LocalOnly` set to `true`.
- Set `Bridge.IP` when there are multiple headsets on the network or when the accepted range should be narrowed. If only one type of sender is used, `Source` can be fixed to `Bridge` or `Streaming` so that the other ports are not opened.

## Before sharing logs or configuration

- Replace IP addresses and paths containing the user name with placeholders.
- Keep only the entries relevant to the issue from migration reports and the `Migration` field of the configuration.
- When reporting a configuration issue, include only the relevant fields rather than the complete configuration.
- Do not publish raw packet captures or recordings of tracking data.

## Release package

The module package contains a freshly generated default `UnifiedPicoModule.json`, so it carries no personal IP addresses, migration records or local paths. The compiled module does not record the build machine's user directory, and `sdk-provenance.json` lists only the SDK commit, assembly version and file digests. When sharing your own configuration or logs, follow the checklist above.

## MouthLink (fork addition)

With a MouthLink model installed, the module also handles **mouth-camera images**: infrared pictures of the lower face, i.e. biometric data.

- Frames are received over UDP from the headset (PicoET-Enhance-MouthLink), processed in memory and dropped. Nothing is written to disk and nothing leaves the PC; the model's output goes to VRCFT like any other expression weight.
- The stream is unencrypted. Anyone who can read your LAN traffic (for example with the Wi-Fi password) can see it. Use a trusted network; never forward UDP 9040/9041 from the internet.
- The headset accepts SUBSCRIBE only from private addresses by default; restrict it to this PC with `picoet.sh mouthlink-allow <PC-IP>`. An active stream cannot be taken over by another address.
- The module accepts frames only from the headset it subscribed to and DISCOVER answers only from private addresses; the loopback input (`udp` source) accepts only `127.0.0.1`.
- The log contains the headset address (`subscribing to the headset at …`). Remove it before sharing logs.
- A model (`mouth.onnx`) is trained on your face and is executed by ONNX Runtime: treat models like programs — use only your own, and do not publish yours.
