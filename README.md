# KytyPS5 Launcher

A packaged Windows x64 WinUI 3 application that checks the latest KytyPS5 release and installs it on request.

## Development

- Windows x64, .NET 11 SDK (11.0.100 or a later .NET 11 feature band), and WinApp CLI 0.7 or later.
- Enable Windows Developer Mode to register and run the development package. Enabling it requires administrator approval.
- From this folder, build with `dotnet build .\KytyPS5Launcher\KytyPS5Launcher.csproj -p:Platform=x64`.
- Run with `winapp run .\KytyPS5Launcher --arch x64`. This builds and registers a development package.
- Create an unsigned, self-contained x64 MSIX with `winapp package .\KytyPS5Launcher\KytyPS5Launcher.csproj --arch x64 --no-sign`. Installation requires signing with a trusted certificate; do not install a certificate without administrator approval.
- For local testing, generate a development certificate with `winapp cert generate --manifest .\KytyPS5Launcher\Package.appxmanifest`, then package with `winapp package .\KytyPS5Launcher\KytyPS5Launcher.csproj --arch x64 --cert .\devcert.pfx`. Keep the private certificate out of source control. A development certificate is not suitable for distribution; use a trusted publisher certificate for production.

## Using the launcher

Select **Check for updates**, then **Install** or **Update**. The download is checked against the release's published SHA-256 digest before extraction. If the release has no Windows x64 ZIP or SHA-256 digest, installation is refused. Select **Launch KytyPS5** to start its graphical launcher. Close KytyPS5, its games, and its updater before installing an update. Disable KytyPS5's own automatic updater to avoid changes outside this launcher.

The default installation folder is `%LOCALAPPDATA%\KytyPS5Launcher`. Select **Change folder** to choose an empty folder before or after installation. Close KytyPS5 and its games first; changing folders copies all managed versions and their data, leaves the original intact, and requires enough free space for the copy. If copying fails, the original remains active; the selected folder may contain partial files. The selected location is saved in `%LOCALAPPDATA%\KytyPS5Launcher\location.json`.

The selected folder contains `versions` and a `current.json` identifying the active version. A portable `Kyty.ini` is created on first install, and the previous installation's files are copied to the new version before its release files are applied. Existing `Kyty.ini` is retained. The previous version is kept if installation fails, and old versions are not automatically deleted. They consume disk space; back up any data you need before manually removing them. The launcher manages only installations it created and does not import an existing installation.

Each update copies the previous installation, so sufficient free space is needed for both versions and the downloaded archive. The digest verifies the archive against the metadata supplied by GitHub; it is not an independent publisher signature.
