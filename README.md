# secbox

Anti-malware / sandbox-review library for the s&box editor.

## Why

> "Editor projects are not sandboxed. They are not limited by any whitelists
> and can run any functions. You should be careful when running code you have
> received from an untrusted source - because it can do almost anything."
> — official s&box docs, `editor/editor-project.md`

When you install a library from the s&box Library Manager, its editor-side
assembly (`<ident>.editor.dll`) gets loaded into your editor process with
full host privileges: `System.IO.File.Delete`, `System.Diagnostics.Process.Start`,
P/Invoke into `kernel32`, raw sockets, the lot. Game-side code is gated by
the engine's `AccessControl` whitelist; editor-side code is not.

secbox is a defensive library that scans every other library you install
for these dangerous APIs and prompts you to allow / block before damage
happens.

## What it does

1. **Scans on install.** Hooks `PackageManager.OnPackageInstalledToContext`
   (reflectively — the type is engine-internal). When a library is
   downloaded, secbox scans its `.dll` and `.cs` files before the editor
   loads them, opens a modal showing the findings, and waits for your
   decision.

2. **Audits on boot.** Walks `LibrarySystem.All` at editor startup
   (`[Event("editor.created")]`) and re-scans any library whose content
   hash isn't already trusted or blocked.

3. **Monitors at runtime.** Subscribes to `AppDomain.AssemblyLoad` and
   scans any third-party assembly that appears later — catches anything
   that slipped past the first two layers.

## What it blocks

Findings are graded into four severities:

- **Critical** — patterns no legitimate editor tool needs:
  - `[DllImport]` / `[LibraryImport]` (P/Invoke into native code)
  - `System.Diagnostics.Process.Start`
  - Dynamic assembly loading (`Assembly.LoadFile`, `AssemblyLoadContext`)
  - `System.Reflection.Emit.*`, `CSharpScript.*`
  - `Marshal.GetDelegateForFunctionPointer`
  - Unmanaged native binaries (`.so`, `.dylib`, native `.dll`) shipped
    in the package
  - String literals matching Win32 attack-tool names (`kernel32`,
    `powershell`, `cmd.exe`, `VirtualAlloc`, `CreateRemoteThread`, …)

- **High** — dangerous APIs that are sometimes legitimate:
  - Direct `System.IO.File.*` / `Directory.*` / `FileStream`
  - Raw `System.Net.Sockets`, `WebClient`, `WebRequest`
  - Reflection-based dynamic invocation (`MethodInfo.Invoke`,
    `Activator.CreateInstance` on dynamic types)
  - `Microsoft.Win32.Registry`
  - Pinned local variables (unverifiable code)

- **Medium** — anything the engine's own `AccessControl` whitelist would
  reject if this code were game-side. Most legitimate editor extensions
  trip a few of these.

- **Low** — `using` directives importing suspect namespaces, attributes
  not on the engine whitelist. Triage-only.

## What it cannot do

Be honest with yourself before you ship this:

1. **Load-order race.** If a malicious package was already installed and
   loaded *before* secbox ran in this project, its static constructors
   have already executed. secbox will detect it on the next editor boot
   and offer to uninstall, but cannot undo whatever the static ctor did
   the first time. Install secbox FIRST in a new project.

2. **Same-process attack surface.** secbox runs in the same unsandboxed
   editor context as every other library. A package loaded before secbox
   could in theory `File.Delete` secbox's trust store, hook our dialog
   via reflection, or shim `Type.GetType("Sandbox.SecBox.*")` lookups.
   Real defense against this needs engine-side support from Facepunch.

3. **Static analysis is bypassable.** Obfuscation, dynamic codegen via
   `CSharpScript`, fetching a payload over HTTPS and `Assembly.Load`-ing
   it, calling engine APIs that themselves wrap unsafe operations — all
   defeat naive scans. We catch the lazy 95% directly and flag the rest
   ("uses dynamic code loading at all" is always Critical regardless of
   payload), but a serious adversary will route around.

4. **Cannot truly block on install.** The current modal isn't a true
   synchronous gate — Qt's `DisplayDialog` is modal-input but doesn't
   block the calling thread. By the time you click a button, the
   package's static ctors may have run. The dialog records your
   decision so future installs of the same content hash are auto-blocked,
   but the first install is best-effort. v0.2 will use a custom
   `Window.Exec()` modal for true synchronous blocking.

5. **Native DLL opacity.** A package shipping a native binary
   (`.so`/`.dylib`/native `.dll`) is opaque to .NET-IL scanners. Policy
   default is to flag any unmanaged binary as Critical regardless of
   what it does.

## Trust store

Decisions persist to `<projectRoot>/.secbox/trust.json`. Key is the
SHA-256 content hash of every `.dll`, `.cs`, `.razor`, `.cshtml` file in
the package — version bumps with no content change keep their trust,
any byte change forces a re-prompt.

The file is hand-editable. Revoke a decision by deleting its entry.

## Menu entries

- **Editor → secbox → Scan All Libraries Now** — re-run boot audit on
  demand. Useful if you installed secbox into an existing project.
- **Editor → secbox → Show Unreviewed Findings…** — list packages
  flagged by the scanner but not yet decided.
- **Editor → secbox → Open Trust Store File** — opens
  `.secbox/trust.json` in your default editor.

## Architecture

```
Editor/
  Lifecycle/
    SecboxBoot.cs          ModuleInitializer (earliest entry)
    InstallHook.cs         Reflective hook into PackageManager
    BootAudit.cs           [Event("editor.created")] sweep
    RuntimeMonitor.cs      AppDomain.AssemblyLoad subscriber
    PackageLocator.cs      Resolves Package → on-disk folder
    ReflectionHelpers.cs   Engine-internal access (audit surface)
  Scanner/
    AssemblyScanner.cs     Mono.Cecil IL walker
    SourceScanner.cs       Roslyn syntax walker (binary scanner is
                           authoritative; source is triage)
    PackageScanner.cs      Orchestrator over a package folder
    MemberKey.cs           Engine-format key generator
    Finding.cs / Severity.cs
    Rules/
      RuleSet.cs           Regex matching (mirrors AccessRules.cs)
      EngineRules.cs       Port of engine's game-side AccessControl
      CriticalRules.cs     Always-flag attack patterns
  Trust/
    TrustStore.cs          .secbox/trust.json persistence
    TrustEntry.cs / Decision.cs / Policy.cs
    PackageHasher.cs       SHA-256 over package contents
  UI/
    ReviewDialog.cs        Modal review (Qt DisplayDialog v0.1)
    MenuItems.cs           [Menu] entries
```

## Acknowledgements

The `Editor/Scanner/Rules/EngineRules.cs` rule set is a port of
`sbox-public/engine/Sandbox.Access/Rules/*.cs`. When Facepunch updates
those rules, re-port to keep parity.

## Reporting security issues

If you find a way to evade secbox's detection, please contact the maintainer
privately rather than opening a public issue.
