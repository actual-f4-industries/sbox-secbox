using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox.SecBox.Bridge;
using Sandbox.SecBox.Bridge.Dto;

namespace Sandbox.SecBox.UI;

// Entry point used by InstallHook / BootAudit. Marshals to the editor main
// thread via MainThread.Queue, then either creates a new ReviewWindow for
// the package or merges findings into an existing window (avoids spawning
// duplicate dialogs when the engine fires multiple install events for the
// same package across different tags).
public static class ReviewDialog
{
	// One open window per package ident. Engine fires install events with
	// gamemenu/game/local/tool tags — that's up to 4 dialogs without dedup.
	// Keyed by ident lower-case for stability.
	static readonly Dictionary<string, ReviewWindow> _openWindows =
		new(StringComparer.OrdinalIgnoreCase);

	public static void Show(
		string packageIdent,
		string contentHash,
		IList<Finding> findings,
		TrustStore store)
	{
		MainThread.Queue(() =>
		{
			try
			{
				if (_openWindows.TryGetValue(packageIdent, out var existing) && existing != null)
				{
					try
					{
						existing.MergeFindings(contentHash, findings);
						DiagnosticsLog.Trace($"merged {findings?.Count ?? 0} findings into open review window for {packageIdent}");
						return;
					}
					catch (Exception mergeEx)
					{
						DiagnosticsLog.Warn($"merge into existing window failed for {packageIdent}: {mergeEx.Message}; opening fresh");
						_openWindows.Remove(packageIdent);
					}
				}

				ReviewWindow window = null;
				window = new ReviewWindow(
					packageIdent, contentHash, findings, store,
					onDecision: decision =>
					{
						RecordDecision(store, packageIdent, contentHash, GetCurrentFindings(window, findings), decision);
						_openWindows.Remove(packageIdent);
					});

				window.Destroyed += () => _openWindows.Remove(packageIdent);
				_openWindows[packageIdent] = window;
				window.Show();
			}
			catch (Exception ex)
			{
				DiagnosticsLog.Error("ReviewWindow construction threw", ex);
				_openWindows.Remove(packageIdent);
				try
				{
					EditorUtility.DisplayDialog(
						$"secbox — {packageIdent}",
						BuildFallbackText(packageIdent, contentHash, findings),
						icon: "⚠️");
				}
				catch { }
			}
		});
	}

	// Pull the merged findings off the window when the user clicks a button —
	// otherwise we'd persist only the initial-scan subset.
	static IList<Finding> GetCurrentFindings(ReviewWindow window, IList<Finding> fallback)
	{
		try
		{
			var cur = window?.CurrentFindings;
			if (cur == null) return fallback;
			return cur as IList<Finding> ?? cur.ToList();
		}
		catch { return fallback; }
	}

	static string BuildFallbackText(string ident, string hash, IList<Finding> findings)
	{
		var critical = findings.Count(f => f.Severity == Severity.Critical);
		var high = findings.Count(f => f.Severity == Severity.High);
		var medium = findings.Count(f => f.Severity == Severity.Medium);
		var low = findings.Count(f => f.Severity == Severity.Low);

		return $"Package: {ident}\nHash: {hash[..16]}…\n\n"
			+ $"Findings: Critical={critical} High={high} Medium={medium} Low={low}\n\n"
			+ string.Join("\n", findings.OrderByDescending(f => f.Severity).Take(10)
				.Select(f => $"  [{f.Severity}] {f.RuleId} @ {Trunc(f.Location, 90)}"))
			+ "\n\nReview decision was not recorded — see secbox log.";
	}

	static string Trunc(string s, int max) =>
		string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

	static void RecordDecision(
		TrustStore store,
		string ident,
		string hash,
		IList<Finding> findings,
		Decision decision)
	{
		if (decision == Decision.NotReviewed) return; // user clicked "decide later"

		var critical = findings.Count(f => f.Severity == Severity.Critical);
		var high = findings.Count(f => f.Severity == Severity.High);
		var medium = findings.Count(f => f.Severity == Severity.Medium);
		var low = findings.Count(f => f.Severity == Severity.Low);

		store.Upsert(new TrustEntry
		{
			PackageIdent = ident,
			ContentHash = hash,
			Decision = decision,
			ReviewedAt = DateTime.UtcNow,
			CriticalCount = critical,
			HighCount = high,
			MediumCount = medium,
			LowCount = low,
			Notes = decision switch
			{
				Decision.TrustAlways => "User trusted via review dialog.",
				Decision.AllowOnce   => "User allowed for this session.",
				Decision.Block       => "User blocked via review dialog.",
				Decision.Quarantine  => "User quarantined via review dialog.",
				_                    => null,
			},
		});
		store.Save();

		DiagnosticsLog.Info($"user decision for {ident} ({hash[..16]}…): {decision}");
	}
}
