# Jungle Leopard Screen Control 1.1.0

Windows x64 release package.

## Highlights

- The interface is available in Portuguese or English, based on the Windows display language.
- The `.ht` theme editor supports live previews, editable layers, and hardware and media data.
- Steam integration detects the game in the foreground and selects portrait or landscape artwork to match the LCD orientation.
- SteamGridDB provides a fallback when Steam does not have compatible artwork. An API key is optional; configure it under **Integrations > SteamGridDB**. Windows protects the key for the current user account.
- Built-in hardware sensor readings are available. The package includes the PawnIO installer for sensors that require a low-level driver.
- PresentMon measures foreground game FPS internally. RTSS is not required.
- Supports multiple panels, compositing, media playback, and compatible firmware updates.

## Fixes and improvements

- Improved Steam artwork discovery, including local `appinfo.vdf` data, the Steam CDN, and public artwork URLs before trying the community fallback.
- FPS measurement now uses the time between game presents (`MsBetweenPresents`) instead of the time between frame starts.
- Improved PresentMon ETW session shutdown and cleanup, with retries after startup failures.
- Added FPS capture status showing the foreground process, received frames, and errors.
- Improved CPU/GPU sensor availability and theme preview updates.

## Requirements and installation

- Windows 10 or Windows 11, x64.
- Extract the entire ZIP archive and run `JungleLeopardScreenControl.exe`. The package includes the .NET 10 runtime.
- To find artwork through SteamGridDB, configure an API key under **Integrations > SteamGridDB**. Steam artwork lookup works without this key.
- Some low-level sensors require Windows authorization to install PawnIO. FPS capture may prompt for authorization to start PresentMon.

## Licenses

See `LICENSE`, `THIRD-PARTY-NOTICES.md`, and the included `LICENSES` folder.
