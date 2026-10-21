# Third-party notices

Jungle Leopard Screen Control is licensed under the MIT License in `LICENSE`. The following notices apply to libraries included in Windows x64 builds.

## LibreHardwareMonitorLib 0.9.6

Used for optional live hardware telemetry in LCD themes. The application uses the library directly and does not require the Jungle Leopard software.

- License: Mozilla Public License 2.0 (MPL-2.0)
- Source: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
- Package: https://www.nuget.org/packages/LibreHardwareMonitorLib/0.9.6
- Packaged source revision: `3d331e3370efb858411f19511373eff65a218701`

## Runtime dependencies of LibreHardwareMonitorLib

- `BlackSharp.Core` 1.0.7 — MPL-2.0 — https://github.com/Blacktempel/BlackSharp
- `DiskInfoToolkit` 1.1.2 — MPL-2.0 — https://github.com/Blacktempel/DiskInfoToolkit
- `HidSharp` 2.6.4 — Apache-2.0 — https://software.seekye.com/hidsharp — license text: `LICENSES/HidSharp-Apache-2.0.txt`
- `Mono.Posix.NETStandard` 1.0.0 — Microsoft package license terms: https://go.microsoft.com/fwlink/?linkid=869050
- `RAMSPDToolkit-NDD` 1.4.2 — MPL-2.0 — https://github.com/Blacktempel/RAMSPDToolkit
- `System.IO.Ports` 10.0.3 — MIT — https://github.com/dotnet/runtime
- `System.Management` 10.0.2 — MIT — https://github.com/dotnet/runtime

Some hardware sensors may require elevated access or driver support. The app leaves unavailable readings blank. Windows x64 is the default build target; builders can override `RuntimeIdentifier` for another supported Windows architecture.

## PresentMon 2.6.0

The app bundles the PresentMon console capture executable to read frame presentation timing for the foreground application when an FPS theme sensor is in use. The broker starts only for themes that request this sensor and requests Windows elevation through UAC because PresentMon needs an ETW trace session. No separate PresentMon installation is required.

- Copyright: Intel Corporation, 2017–2024
- License: MIT — `ThirdParty/PresentMon/LICENSE.txt`
- Source: https://github.com/GameTechDev/PresentMon
- Bundled executable SHA-256: `B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF`
