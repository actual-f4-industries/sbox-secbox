using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Editor;
using Sandbox.SecBox.Bridge;
using Sandbox.SecBox.Bridge.Dto;
using DiagnosticsLog = Sandbox.SecBox.Bridge.DiagnosticsLog;

namespace Sandbox.SecBox.Lifecycle;

// Subscribes to PackageManager.OnPackageInstalledToContext via reflection
// (PackageManager is internal - no public alternative for code packages).
// Tries to insert at the front of the delegate chain so we run before
// ToolsDll/GameInstanceDll, giving us a window to scan + prompt before the
// new package's assemblies load. Falls back to appending if the field-level
// trick fails on a future engine version.
//
// Scans are delegated to Secbox.Core via SecboxCoreClient - secbox.editor.dll
// itself is just an adapter; all heavy detection logic lives in the
// downloaded core DLL.
public static class InstallHook
{
	const string EventName = "OnPackageInstalledToContext";
	static bool subscribed;

	public static void Subscribe()
	{
		if (subscribed) return;

		var pmType = ReflectionHelpers.PackageManagerType();
		if (pmType == null)
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Warning(
				"[secbox] PackageManager type not found - install hook disabled");
			return;
		}

		var ev = pmType.GetEvent(EventName, BindingFlags.Public | BindingFlags.Static);
		if (ev == null)
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Warning(
				$"[secbox] {EventName} event not found on PackageManager - install hook disabled");
			return;
		}

		var ourMethod = typeof(InstallHook).GetMethod(nameof(OnPackageInstalled),
			BindingFlags.NonPublic | BindingFlags.Static);

		Delegate handler;
		try { handler = Delegate.CreateDelegate(ev.EventHandlerType, ourMethod); }
		catch (Exception ex)
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Warning(
				$"[secbox] could not bind install handler: {ex.Message}");
			return;
		}

		if (ReflectionHelpers.InsertFirstInChain(pmType, EventName, handler))
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Info(
				"[secbox] install hook armed (first-in-chain)");
		}
		else if (ReflectionHelpers.AppendToChain(pmType, EventName, handler))
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Warning(
				"[secbox] install hook armed (appended - pre-load gating may not work)");
		}
		else
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Warning(
				"[secbox] install hook could not subscribe");
			return;
		}

		subscribed = true;
	}

	static void OnPackageInstalled(object activePackage, string tag)
	{
		DiagnosticsLog.Trace($"InstallHook.OnPackageInstalled: tag={tag}");
		try { HandleInstall(activePackage, tag); }
		catch (Exception ex)
		{
			DiagnosticsLog.Error("install handler threw", ex);
		}
		DiagnosticsLog.Trace($"InstallHook.OnPackageInstalled: end tag={tag}");
	}

	static void HandleInstall(object activePackage, string tag)
	{
		var pkg = ReflectionHelpers.GetProp(activePackage, "Package") as Package;
		if (pkg == null) return;

		var ident = pkg.FullIdent ?? pkg.Ident ?? "<unknown>";

		// Skip the obvious: secbox itself, engine first-party packages.
		if (ident.StartsWith("local.secbox", StringComparison.OrdinalIgnoreCase)) return;
		if (ident.Equals("secbox", StringComparison.OrdinalIgnoreCase)) return;
		if (ident.StartsWith("facepunch.", StringComparison.OrdinalIgnoreCase)) return;

		var projectRoot = PackageLocator.CurrentProjectRoot();
		if (string.IsNullOrEmpty(projectRoot)) return;

		// Skip the project's own package - it loads under "gamemenu" / "local"
		// tags during editor boot and would otherwise trigger a full
		// project-root scan. The current project's ident comes from its sbproj.
		try
		{
			var currentProjIdent = Project.Current?.Package?.FullIdent
				?? Project.Current?.Package?.Ident;
			if (!string.IsNullOrEmpty(currentProjIdent)
			    && ident.Equals(currentProjIdent, StringComparison.OrdinalIgnoreCase))
			{
				DiagnosticsLog.Trace($"skipping install of current project itself: {ident}");
				return;
			}
		}
		catch { }

		// Locate the package's library folder. FolderFor only returns paths
		// strictly under <projectRoot>/Libraries/ - engine packages and the
		// project itself correctly return null here.
		var folder = PackageLocator.FolderFor(pkg);
		if (string.IsNullOrEmpty(folder))
		{
			DiagnosticsLog.Trace($"skipping {ident} tag={tag}: no library folder resolved (engine/external package)");
			return;
		}

		DiagnosticsLog.Info($"package install: ident={ident} tag={tag} folder={folder}");

		var store = TrustStore.Load(projectRoot);
		if (!store.Policy.ScanOnInstall) return;

		// Defence-in-depth: refuse to ever scan a project-root-shaped folder
		// even if PackageLocator slipped up.
		if (string.Equals(folder, projectRoot, StringComparison.OrdinalIgnoreCase))
		{
			DiagnosticsLog.Warn($"refusing to scan project root for {ident}");
			return;
		}

		var hash = PackageHasher.HashFolder(folder);
		var existing = store.Find(hash);
		if (existing != null)
		{
			switch (existing.Decision)
			{
				case Decision.TrustAlways:
					global::Sandbox.Internal.GlobalGameNamespace.Log.Info(
						$"[secbox] {ident} matches TrustAlways entry; allowing");
					return;
				case Decision.Block:
					global::Sandbox.Internal.GlobalGameNamespace.Log.Error(
						$"[secbox] {ident} matches Block entry; refuse-install path TBD");
					return;
			}
		}

		// Bridge call. Runs on a thread-pool thread (Task.Run) to break any
		// SynchronizationContext capture the engine thread might have set -
		// without this, the async continuations inside EnsureReadyAsync /
		// the Core's internal GetAwaiter().GetResult() deadlock against the
		// hook thread we'd be blocking here.
		try
		{
			Task.Run(() => SecboxCoreClient.EnsureReadyAsync()).GetAwaiter().GetResult();
		}
		catch (Exception ex)
		{
			DiagnosticsLog.Error("core load failed", ex);
			return;
		}

		ScanReport report;
		try
		{
			report = Task.Run(() => SecboxCoreClient.ScanFolder(folder)).GetAwaiter().GetResult();
		}
		catch (Exception ex)
		{
			DiagnosticsLog.Error("scan threw", ex);
			return;
		}

		var critical = report.Findings.Count(f => f.Severity == Severity.Critical);
		var high = report.Findings.Count(f => f.Severity == Severity.High);
		var medium = report.Findings.Count(f => f.Severity == Severity.Medium);
		var low = report.Findings.Count(f => f.Severity == Severity.Low);

		global::Sandbox.Internal.GlobalGameNamespace.Log.Info(
			$"[secbox] scan {ident}: Critical={critical} High={high} Medium={medium} Low={low} overall={report.Overall}");

		var maxSeverity = report.Findings.Count == 0
			? Severity.Info
			: report.Findings.Max(f => f.Severity);

		if (maxSeverity >= store.Policy.PromptThreshold)
		{
			store.Upsert(new TrustEntry
			{
				PackageIdent = ident,
				Version = pkg.Revision?.VersionId.ToString(),
				ContentHash = hash,
				Decision = Decision.NotReviewed,
				ReviewedAt = DateTime.UtcNow,
				CriticalCount = critical,
				HighCount = high,
				MediumCount = medium,
				LowCount = low,
				Notes = $"Auto-recorded by install hook. First 5 findings:\n"
					+ string.Join("\n", report.Findings.Take(5).Select(f => "  " + f)),
			});
			store.Save();

			global::Sandbox.Internal.GlobalGameNamespace.Log.Warning(
				$"[secbox] {ident}: {critical} critical / {high} high findings - opening review dialog");

			Sandbox.SecBox.UI.ReviewDialog.Show(ident, hash, report.Findings, store);
		}
	}
}
