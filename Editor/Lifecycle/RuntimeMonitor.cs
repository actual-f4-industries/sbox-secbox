using System;
using System.Linq;
using System.Threading.Tasks;
using Sandbox.SecBox.Bridge;
using Sandbox.SecBox.Bridge.Dto;
using DiagnosticsLog = Sandbox.SecBox.Bridge.DiagnosticsLog;

namespace Sandbox.SecBox.Lifecycle;

// Subscribes to AppDomain.CurrentDomain.AssemblyLoad. Late-detection layer:
// by the time this fires the assembly's static ctors have already run, so we
// can warn but cannot prevent the initial payload. The install hook is the
// only layer that gates pre-load.
//
// Defers scanning to Secbox.Core via the bridge. EnsureReadyAsync is only
// called the first time we actually need to scan something.
public static class RuntimeMonitor
{
	static bool subscribed;

	public static void Subscribe()
	{
		if (subscribed) return;
		subscribed = true;

		try
		{
			AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
			global::Sandbox.Internal.GlobalGameNamespace.Log.Info(
				"[secbox] runtime monitor armed");
		}
		catch (Exception ex)
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Warning(
				$"[secbox] runtime monitor could not subscribe: {ex.Message}");
		}
	}

	static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
	{
		try { Handle(args.LoadedAssembly); }
		catch (Exception ex) { DiagnosticsLog.Error("runtime monitor threw on assembly load", ex); }
	}

	static void Handle(System.Reflection.Assembly asm)
	{
		if (asm == null) return;

		var name = asm.GetName().Name ?? "";
		var location = asm.Location;

		// Gate by LOCATION not name. Only scan assemblies loaded from the
		// current project's Libraries/ tree - that's where third-party editor
		// libraries live. Anything from the engine bin (HarfBuzzSharp, ExCSS,
		// Sandbox.*, etc.) or from secbox's own ALC cache is skipped.
		if (!IsUserLibraryAssembly(name, location))
		{
			DiagnosticsLog.Trace($"runtime monitor: skipping {name} (not under Libraries/)");
			return;
		}

		DiagnosticsLog.Info($"runtime monitor: scanning newly-loaded assembly {name} @ {location}");

		var projectRoot = PackageLocator.CurrentProjectRoot();
		var store = !string.IsNullOrEmpty(projectRoot)
			? TrustStore.Load(projectRoot)
			: new TrustStore { Policy = new Policy() };

		if (!store.Policy.RuntimeMonitor) return;

		try
		{
			Task.Run(() => SecboxCoreClient.EnsureReadyAsync()).GetAwaiter().GetResult();
		}
		catch (Exception ex)
		{
			DiagnosticsLog.Warn($"runtime monitor: core load failed, skipping scan of {name}: {ex.Message}");
			return;
		}

		ScanReport report;
		try { report = Task.Run(() => SecboxCoreClient.ScanAssembly(location)).GetAwaiter().GetResult(); }
		catch (Exception ex)
		{
			DiagnosticsLog.Warn($"runtime monitor: scan threw for {name}: {ex.Message}");
			return;
		}

		var critical = report.Findings.Count(f => f.Severity == Severity.Critical);
		var high = report.Findings.Count(f => f.Severity == Severity.High);

		if (critical > 0)
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Error(
				$"[secbox] runtime monitor: {name} has {critical} CRITICAL findings "
				+ $"AFTER load - static ctor damage may already be done. "
				+ $"First finding: {report.Findings.First(f => f.Severity == Severity.Critical)}");
		}
		else if (high > 0)
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Warning(
				$"[secbox] runtime monitor: {name} has {high} high-severity findings (post-load)");
		}
	}

	// True iff the assembly was loaded from a path strictly under
	// <projectRoot>/Libraries/<sub>/. That's the only place user-installed
	// editor extensions live; everything else is engine, NuGet, or our own
	// downloaded core ALC.
	static bool IsUserLibraryAssembly(string assemblyName, string location)
	{
		if (string.IsNullOrEmpty(location)) return false;

		// secbox itself - never scan our own.
		if (assemblyName.StartsWith("secbox", StringComparison.OrdinalIgnoreCase)) return false;
		if (assemblyName.StartsWith("Secbox.", StringComparison.OrdinalIgnoreCase)) return false;

		var projectRoot = PackageLocator.CurrentProjectRoot();
		if (string.IsNullOrEmpty(projectRoot)) return false;

		string libRoot;
		try
		{
			libRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(projectRoot, "Libraries"))
				.TrimEnd(System.IO.Path.DirectorySeparatorChar)
				+ System.IO.Path.DirectorySeparatorChar;
		}
		catch { return false; }

		try
		{
			var full = System.IO.Path.GetFullPath(location);
			return full.StartsWith(libRoot, StringComparison.OrdinalIgnoreCase);
		}
		catch { return false; }
	}
}
