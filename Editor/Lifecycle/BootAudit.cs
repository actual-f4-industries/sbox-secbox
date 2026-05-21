using System;
using System.Linq;
using System.Threading.Tasks;
using Editor;
using Sandbox.SecBox.Bridge;
using Sandbox.SecBox.Bridge.Dto;
using DiagnosticsLog = Sandbox.SecBox.Bridge.DiagnosticsLog;

namespace Sandbox.SecBox.Lifecycle;

// Walks LibrarySystem.All, scans every library that doesn't already have a
// TrustAlways or Block decision recorded for its current content hash.
// Hits Secbox.Core via SecboxCoreClient.
public static class BootAudit
{
	static bool ranThisSession;

	[Event("editor.created")]
	public static void OnEditorCreated(object _)
	{
		if (ranThisSession) return;
		ranThisSession = true;
		Run();
	}

	public static void Run()
	{
		DiagnosticsLog.Trace("BootAudit.Run: begin");
		try { RunImpl(); }
		catch (Exception ex) { DiagnosticsLog.Error("boot audit threw", ex); }
		DiagnosticsLog.Trace("BootAudit.Run: end");
	}

	static void RunImpl()
	{
		var projectRoot = PackageLocator.CurrentProjectRoot();
		if (string.IsNullOrEmpty(projectRoot))
		{
			DiagnosticsLog.Warn("boot audit: no current project — abort");
			return;
		}
		DiagnosticsLog.Trace($"boot audit: projectRoot={projectRoot}");

		var store = TrustStore.Load(projectRoot);
		if (!store.Policy.ScanOnBoot)
		{
			DiagnosticsLog.Info("boot audit: ScanOnBoot policy is OFF — abort");
			return;
		}

		var libraries = LibrarySystem.All?.ToList();
		if (libraries == null || libraries.Count == 0)
		{
			DiagnosticsLog.Info("boot audit: LibrarySystem.All is empty — no libraries to scan");
			return;
		}

		DiagnosticsLog.Info($"boot audit: walking {libraries.Count} library projects");

		try
		{
			Task.Run(() => SecboxCoreClient.EnsureReadyAsync()).GetAwaiter().GetResult();
		}
		catch (Exception ex)
		{
			DiagnosticsLog.Error("boot audit: core load failed — abort", ex);
			return;
		}

		int newlyFlagged = 0;
		int reviewedSkipped = 0;
		int scanned = 0;
		int skippedNoIdent = 0;
		int skippedSelf = 0;
		int skippedNoFolder = 0;
		int skippedNotInLibraries = 0;

		foreach (var lib in libraries)
		{
			string ident = null;
			string libRootPath = null;
			try
			{
				// LibraryProject.Project is a public property — use it directly.
				var proj = lib?.Project;
				ident = proj?.Package?.FullIdent ?? proj?.Package?.Ident;
				libRootPath = proj?.RootDirectory?.FullName;
			}
			catch (Exception ex)
			{
				DiagnosticsLog.Warn($"boot audit: failed to introspect library: {ex.Message}");
				continue;
			}

			if (string.IsNullOrEmpty(ident))
			{
				DiagnosticsLog.Trace($"boot audit: skip lib with empty ident (path={libRootPath ?? "<null>"})");
				skippedNoIdent++;
				continue;
			}

			// Self-skip is content-based: don't rely on ident shape. Engine's
			// Package.FormatIdent emits "{org}.{ident}#local" for local packages
			// (e.g. "f4industries.secbox#local"), which no prefix filter would
			// reliably catch across forks/renames. The sbproj presence is the
			// ground truth.
			if (!string.IsNullOrEmpty(libRootPath)
			    && System.IO.File.Exists(System.IO.Path.Combine(libRootPath, "secbox.sbproj")))
			{
				DiagnosticsLog.Trace($"boot audit: skip secbox-own ({ident}) at {libRootPath}");
				skippedSelf++;
				continue;
			}

			if (string.IsNullOrEmpty(libRootPath) || !System.IO.Directory.Exists(libRootPath))
			{
				DiagnosticsLog.Trace($"boot audit: skip {ident} — no folder on disk (path={libRootPath ?? "<null>"})");
				skippedNoFolder++;
				continue;
			}

			// Defence-in-depth: only scan paths under the open project's Libraries/.
			var libRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(projectRoot, "Libraries"))
				.TrimEnd(System.IO.Path.DirectorySeparatorChar)
				+ System.IO.Path.DirectorySeparatorChar;
			var fullLibPath = System.IO.Path.GetFullPath(libRootPath);
			if (!fullLibPath.StartsWith(libRoot, StringComparison.OrdinalIgnoreCase))
			{
				DiagnosticsLog.Trace($"boot audit: skip {ident} — not under {libRoot} (was {fullLibPath})");
				skippedNotInLibraries++;
				continue;
			}

			var hash = PackageHasher.HashFolder(libRootPath);
			var existing = store.Find(hash);
			if (existing != null && existing.Decision is Decision.TrustAlways or Decision.Block)
			{
				DiagnosticsLog.Trace($"boot audit: skip {ident} — already decided ({existing.Decision})");
				reviewedSkipped++;
				continue;
			}

			DiagnosticsLog.Info($"boot audit: scanning {ident} at {libRootPath}");

			ScanReport report;
			try
			{
				report = Task.Run(() => SecboxCoreClient.ScanFolder(libRootPath)).GetAwaiter().GetResult();
			}
			catch (Exception ex)
			{
				DiagnosticsLog.Warn($"boot audit: scan of {ident} failed: {ex.Message}");
				continue;
			}

			scanned++;
			var critical = report.Findings.Count(f => f.Severity == Severity.Critical);
			var high = report.Findings.Count(f => f.Severity == Severity.High);
			var medium = report.Findings.Count(f => f.Severity == Severity.Medium);
			var low = report.Findings.Count(f => f.Severity == Severity.Low);

			DiagnosticsLog.Info($"boot audit: {ident}: Critical={critical} High={high} Medium={medium} Low={low}");

			var maxSeverity = report.Findings.Count == 0 ? Severity.Info : report.Findings.Max(f => f.Severity);
			if (maxSeverity >= store.Policy.PromptThreshold)
			{
				store.Upsert(new TrustEntry
				{
					PackageIdent = ident,
					Version = null,
					ContentHash = hash,
					Decision = existing?.Decision ?? Decision.NotReviewed,
					ReviewedAt = DateTime.UtcNow,
					CriticalCount = critical,
					HighCount = high,
					MediumCount = medium,
					LowCount = low,
					Notes = $"Boot audit. First 5 findings:\n"
						+ string.Join("\n", report.Findings.Take(5).Select(f => "  " + f)),
				});
				newlyFlagged++;
			}
		}

		if (newlyFlagged > 0) store.Save();

		DiagnosticsLog.Info(
			$"boot audit done: total={libraries.Count} scanned={scanned} newlyFlagged={newlyFlagged} "
			+ $"alreadyDecided={reviewedSkipped} "
			+ $"skippedNoIdent={skippedNoIdent} skippedSelf={skippedSelf} "
			+ $"skippedNoFolder={skippedNoFolder} skippedNotInLibraries={skippedNotInLibraries}");
	}
}
