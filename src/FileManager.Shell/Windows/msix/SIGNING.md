# Sparse MSIX packaging & signing (spec Appendix B) — deferred to M9

This milestone (M8) ships the **manifest template** (`AppxManifest.xml`), the **`IExplorerCommand`
handler** (`../ExplorerCommandHandler.cs`), and these notes **only**. Building and **signing** the
sparse `.msix` is the highest-uncertainty piece (Appendix B) and is **deferred to M9**. Nothing in the
solution build or CI packages or signs an MSIX, and the **HKCU registry-verb fallback**
(`../RegistryVerbs.cs`) provides the right-click entry on Windows 10 / the classic menu today.

## Why a sparse package

A Windows 11 *top-level* context entry (one that shows without "Show more options") requires an
`IExplorerCommand` handler registered through an MSIX package. A **sparse** package keeps the actual
binaries on disk (next to the installed app, via `AllowExternalContent` + `ExternalLocation`) rather than
inside the `.msix`, so the per-user xcopy/self-contained deployment stays intact.

## Packaging steps (M9)

1. Fill the manifest placeholders: `Identity/@Publisher` (must equal the signing cert subject),
   `Version`, and the `Assets\*` logos.
2. Build the sparse package:
   `makeappx pack /d <staging> /p FileManager.ShellExtension.msix /nv`
   (the staging dir contains only `AppxManifest.xml` + assets; binaries are referenced externally).
3. Register at runtime with the external location pointing at the install dir:
   `Add-AppxPackage -Path FileManager.ShellExtension.msix -ExternalLocation <installDir>`.

## Signing (the open item)

- The package **must be signed** with a certificate **trusted on the target machine** for
  `Add-AppxPackage` to accept it.
- Options: (a) a purchased code-signing certificate from a public CA (production); (b) a
  developer/self-signed certificate added to the machine's Trusted People store (dev/test only).
- Sign with: `signtool sign /fd SHA256 /a /f <cert.pfx> /p <pwd> FileManager.ShellExtension.msix`.
- Plan a CI/release signing step (secure cert storage, e.g. an HSM / Key Vault). This is tracked as the
  Appendix B open item and finalized in **M9**.

## Uninstall

`Remove-AppxPackage FileManager.ShellExtension_...`, plus `RegistryVerbs.Uninstall()` for the fallback
verbs. The combined per-user flow is `RegistrationInstaller.Unregister()`.
