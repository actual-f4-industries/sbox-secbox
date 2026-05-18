using Sandbox.SecBox.Bridge.Dto;

namespace Sandbox.SecBox;

// Global policy. Persisted alongside the trust store. Edit either through
// secbox's settings panel or by hand-editing the JSON file.
public sealed class Policy
{
	// Scan packages immediately when PackageManager.OnPackageInstalledToContext
	// fires. Disabling this means new installs are not gated until next boot.
	public bool ScanOnInstall { get; set; } = true;

	// On editor startup, scan every installed package whose content-hash isn't
	// already in the trust store with a TrustAlways or Block decision.
	public bool ScanOnBoot { get; set; } = true;

	// Subscribe to assembly-add events for late-detection. Even if a package
	// got past pre-install gating, this catches subsequent damage from event
	// handlers and menu callbacks (cannot undo static ctor effects).
	public bool RuntimeMonitor { get; set; } = true;

	// Any Critical finding forces a block-by-default verdict. User must
	// explicitly TrustAlways to override. Off = Critical findings still
	// prompt but allow user to AllowOnce.
	public bool BlockCriticalByDefault { get; set; } = true;

	// Reject packages that ship any unmanaged native binary regardless of
	// other findings. Native code is opaque to the scanner.
	public bool BlockUnmanagedDlls { get; set; } = true;

	// Minimum severity that triggers the review dialog. Findings below this
	// are logged but not interactive.
	public Severity PromptThreshold { get; set; } = Severity.Medium;
}
