using System.Runtime.CompilerServices;
using Sandbox.SecBox.Bridge;
using Sandbox.SecBox.UI;

namespace Sandbox.SecBox.Lifecycle;

// ModuleInitializer fires once when the runtime loads our assembly — the
// earliest possible C# entry point, before any of our static constructors
// run on first use. Use it to arm subscriptions that must be in place
// before any third-party library install can happen.
//
// First action is to install the unhandled-exception handler so anything
// that explodes from here on lands in the persistent diagnostics log,
// even if it hangs or crashes the editor before the engine's log surfaces.
internal static class SecboxBoot
{
	[ModuleInitializer]
#pragma warning disable CA2255 // ModuleInitializer is exactly what we want here
	public static void Init()
#pragma warning restore CA2255
	{
		// Install the safety net BEFORE anything else can throw.
		// Verbose first-chance tracing is opt-in via SecboxConfig.VerboseDiagnostics
		// to avoid flooding the log with engine-internal caught exceptions.
		var cfg = SecboxConfig.Load();
		DiagnosticsLog.InstallUnhandledHandler(verbose: cfg.VerboseDiagnostics);
		DiagnosticsLog.Info($"[secbox] boot start — adapter version {typeof(SecboxBoot).Assembly.GetName().Version}, "
			+ $"core required v{CorePolicy.RequiredProtocolVersion}, dev mode {(CorePolicy.DevModeActive ? "ON" : "off")}, "
			+ $"verbose {(cfg.VerboseDiagnostics ? "ON" : "off")}");

		DiagnosticsLog.Wrap("InstallHook.Subscribe", InstallHook.Subscribe);
		DiagnosticsLog.Wrap("RuntimeMonitor.Subscribe", RuntimeMonitor.Subscribe);
		DiagnosticsLog.Wrap("LibraryManagerInjector.Arm", LibraryManagerInjector.Arm);

		// Tier B (always) + optional Tier A (Sentinel). Both ride on the bridge
		// to Secbox.Core — fire-and-forget so an ALC load failure here doesn't
		// keep us from completing boot. Coordinator handles its own retries.
		System.Threading.Tasks.Task.Run(() =>
		{
			DiagnosticsLog.Wrap("RuntimeMonitorCoordinator.EnsureAttached", RuntimeMonitorCoordinator.EnsureAttached);
		});

		DiagnosticsLog.Info($"[secbox] initialised. Log file: {DiagnosticsLog.FilePath}");
	}
}
