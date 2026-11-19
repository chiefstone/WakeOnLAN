# Repository Guidelines

## Project Structure

`WakeOnLan.sln` groups the Windows Forms desktop app (`WakeOnLan.vbproj`), the command-line app (`Console/WakeOnLanC/`), and supporting projects. The root contains the main forms and wake, shutdown, and listener code. Look in `Schedule/` for scheduled tasks, `Property/` for options, and `EventLogViewer/` for the log viewer. `AutoUpdater.NET/` and `Localization/` contain C# support code. Application assets live in `Resources/`, documentation in `Documentation/`, and Inno Setup scripts in `Installer/`.

## Build and Run

Use Visual Studio on Windows with desktop development tools and the .NET Framework targeting packs required by the projects. The desktop and console apps target .NET Framework 4.6.

- Run `nuget restore WakeOnLan.sln` to restore `packages.config` dependencies, or restore them in Visual Studio.
- Run `msbuild WakeOnLan.sln /p:Configuration=Debug /p:Platform="Any CPU"` from a Visual Studio Developer Command Prompt to build the solution.
- Set WakeOnLan as the startup project and press F5 to debug the GUI. Run `Console\WakeOnLanC\bin\Debug\WakeOnLanC.exe` to exercise the CLI.

## Coding Style and Localization

Use four spaces for VB.NET and C# indentation and follow nearby naming. Use PascalCase for public types and members. Keep form behavior in files such as `Explorer.vb` and use the WinForms designer for changes to generated `.Designer.vb` layout code. Add UI strings to the neutral `.resx` and keep resource keys consistent across culture-specific files such as `Explorer.zh-CN.resx`.

## Testing

The solution has no dedicated automated test project. Build Debug and manually verify the affected GUI or CLI workflow. For wake or shutdown changes, use a designated test machine and record the expected and observed outcomes. For localization changes, open the affected dialog in the target language and check fitting and fallback strings.

## Commits and Pull Requests

Recent commits use short, descriptive subjects such as `Fix "Repeat Interval" and "Enable Sound" in Options dialog`. Keep changes focused. In pull requests, explain the behavior change, link related issues, list the checks performed, and include screenshots for visible UI changes.
