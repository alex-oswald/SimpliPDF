# SimpliPDF — Copilot instructions

SimpliPDF is a lightweight WinUI 3 (Windows App SDK 1.8) desktop app for merging, reordering,
and editing PDF pages. It targets .NET 10 on Windows 10 (1809)+ and follows the MVVM pattern.

## Code style (required)

- **Always use explicit types. Never use `var`.** Spell out the concrete type on every local
  variable, including in `foreach`, `using`, and for LINQ/method results. The only case where
  `var` is unavoidable is an anonymous type (e.g. `new { ... }` from a LINQ projection) — avoid
  writing those. This rule is enforced by `.editorconfig` (analyzer `IDE0008`) and can be applied
  or verified with `dotnet format`.
- Use file-scoped namespaces.
- Nullable reference types and implicit usings are enabled — rely on them and don't add redundant
  `using` directives.
- Prefer target-typed `new()` when the type is already stated on the left
  (e.g. `PdfService _pdf = new();`).
- Use collection expressions (`[]`) for empty or initialized collections.
- MVVM: keep UI state and commands in `ViewModels` using CommunityToolkit.Mvvm source generators
  (`[ObservableProperty]`, `[RelayCommand]`, `partial` properties). Keep code-behind thin.

## Architecture

- `Models/` — plain data types (e.g. `PdfPageItem`).
- `ViewModels/MainViewModel.cs` — application state and commands (MVVM).
- `Services/PdfService.cs` — PDFsharp-based merge / split / rotate.
- `Services/ScanService.cs` — WIA scanner integration. All COM calls run on a dedicated STA thread.
- `Interop/Dispatch.cs` — source-generated `IDispatch` / `IEnumVARIANT` late-binding helper
  (`[GeneratedComInterface]` + `ComVariant`). It replaces `dynamic` so the scanner stays
  trim / AOT friendly. **Do not reintroduce `dynamic`.**
- `Helpers/` — `ThumbnailHelper` (PDF page to `BitmapImage` via `Windows.Data.Pdf`),
  `PrintHelper`, and `Win32FileDialog` (native COM file dialogs).

## Win32 / COM interop (required)

- For any Win32 API, use **Microsoft.Windows.CsWin32** source generation: add the function or type
  name to `SimpliPDF/NativeMethods.txt` and call it through the generated `Windows.Win32.PInvoke`
  class. **Do not hand-write `[DllImport]` or `[LibraryImport]`.**
- CsWin32 runs with `allowMarshaling: true` (see `SimpliPDF/NativeMethods.json`). Do **not** add
  `VARIANT`, `IDispatch`, `IEnumVARIANT`, `DISPPARAMS`, or `EXCEPINFO` to `NativeMethods.txt`
  (they collide with the runtime marshaller and cause duplicate-type build errors). Late-binding
  COM lives in `Interop/Dispatch.cs` instead.

## Build & run

```powershell
.\build.ps1                                                        # arm64 Debug (inner loop)
.\build.ps1 -Architectures x64 -Configuration Release             # x64 Release
.\build.ps1 -Publish -Configuration Release                       # self-contained x64 + arm64
.\build.ps1 -Architectures x64,arm64 -Configuration Release -Msix # signed MSIX packages
```

A platform must be specified for direct `dotnet` invocations because the project defines
`x86;x64;ARM64` (no `AnyCPU`), e.g.:

```powershell
dotnet build SimpliPDF/SimpliPDF.csproj -c Debug -p:Platform=x64
```

CI (`.github/workflows/`) builds signed MSIX packages for x64 and ARM64 on `windows-latest`.

## Trimming / Native AOT

- **Native AOT** is enabled for the self-contained, unpackaged **Release** publish on **x64 and
  ARM64** (`build.ps1 -Publish -Configuration Release`). x86 is unsupported by Native AOT and falls
  back to a self-contained JIT build; **Debug** always publishes JIT. The switch lives in
  `SimpliPDF.csproj` (`SimpliPdfUseAot`, keyed off the `win-x64` / `win-arm64` publish profile); it
  implies trimming and disables ReadyToRun. Building AOT needs the VS **"Desktop development with
  C++"** workload (`link.exe`, ARM64 also needs the C++ ARM64 build tools); the
  `Publish (Native AOT)` workflow builds it on `windows-latest`.
- The **MSIX/Store** package keeps the framework-dependent, **trimmed** (non-AOT) model.
- PDFsharp (6.2.4) carries `[DynamicallyAccessedMembers]` annotations across its reflection paths
  and uses no `Reflection.Emit` / `MakeGenericType` / `DynamicMethod`, so it trims and compiles
  under AOT — but empira does not officially certify AOT, so **smoke-test PDF open / merge / rotate /
  split and image→PDF on an AOT build** after touching `PdfService` or the scan path.
- Keep new code trim / AOT friendly (no `dynamic`, no unbounded reflection, prefer source
  generators).

## Before opening a PR

- Build at least one platform (`-p:Platform=x64`) and confirm it compiles with no new warnings.
- Run `dotnet format SimpliPDF/SimpliPDF.csproj --verify-no-changes` (or `dotnet format` to
  auto-fix) so the explicit-type rule and formatting stay satisfied.
