using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Sandbox.SecBox.Bridge;
using Sandbox.SecBox.Bridge.Dto;
using DiagnosticsLog = Sandbox.SecBox.Bridge.DiagnosticsLog;

namespace Sandbox.SecBox.Lifecycle;

// Orchestrates Tier B (profiler) and optionally Tier A (Sentinel). Wires
// the event sink to DiagnosticsLog + a tiny in-memory ring that the
// SentinelSettingsDialog reads for live preview.
//
// Lifecycle:
//   * EnsureAttachedAsync — idempotent, called after SecboxCoreClient is
//     ready (boot path).
//   * UpdateSettings — call when the user toggles Sentinel on/off in the
//     dialog. Detaches and re-attaches with the new options.
//   * DetachAsync — call on shutdown / dev-mode reload.
public static class RuntimeMonitorCoordinator
{
	// Locks are split so the event hot path doesn't share a lock with the
	// attach lifecycle or with reader queries — Sentinel can push hundreds
	// of events per second and the UI thread must never block on that.
	//
	//  _attachLock — serialises Attach/Detach/Reapply. Cold path.
	//  _writeLock  — protects the _recent queue. Held briefly during enqueue
	//                + snapshot publish. Held ONLY by event-receiver threads.
	//  _snapshot   — volatile reference, atomic publish on each event.
	//                Readers never take any lock; just read the field.
	//                Eliminates the deadlock that hits when the UI thread
	//                tries to acquire a contended lock and sbox's
	//                ExpirableSynchronizationContext pumps re-entrant calls.
	static readonly object _attachLock = new();
	static readonly object _writeLock = new();
	static bool _attached;
	static readonly Queue<RuntimeFinding> _recent = new();
	static volatile RuntimeFinding[] _snapshot = System.Array.Empty<RuntimeFinding>();
	const int RecentMax = 256;

	public static bool IsAttached => System.Threading.Volatile.Read(ref _attached);

	// Lock-free read: just returns the latest snapshot. Updaters publish a
	// new array atomically. Safe against any number of concurrent readers
	// and writers without taking a lock on the read path.
	public static IReadOnlyList<RuntimeFinding> RecentFindings => _snapshot;

	public static int RecentCount => _snapshot.Length;

	public static event Action<RuntimeFinding> FindingReceived;

	public static void EnsureAttached()
	{
		var cfg = SecboxConfig.Load();
		if (!cfg.RuntimeMonitoringEnabled)
		{
			DiagnosticsLog.Info("runtime monitoring disabled in config — skipping attach");
			return;
		}

		try
		{
			Task.Run(() => SecboxCoreClient.EnsureReadyAsync()).GetAwaiter().GetResult();
		}
		catch (Exception ex)
		{
			DiagnosticsLog.Warn($"runtime monitor: core not ready, will retry on next boot: {ex.Message}");
			return;
		}

		AttachOnce(cfg);
	}

	static void AttachOnce(SecboxConfig cfg)
	{
		lock (_attachLock)
		{
			if (_attached) return;
			try
			{
				var opts = new RuntimeSensorOptions
				{
					EnableProfiler = true,
					EnableEtw = cfg.SentinelEnabled,
					EnableManagedHook = true,
					CaptureStack = cfg.CaptureStackOnKernelEvents,
					PathAllowlist = cfg.SentinelPathAllowlist?.Count > 0
						? cfg.SentinelPathAllowlist : null,
					Enforcement = new EnforcementPolicyDto
					{
						BlockLibraryProcessStart = cfg.BlockLibraryProcessStart,
					},
				};
				var result = RuntimeMonitorBridge.Attach(opts, OnFindingJson);
				_attached = result.Attached;

				if (result.Attached)
				{
					DiagnosticsLog.Info($"runtime sensors attached: "
						+ string.Join(", ", result.Sensors.Select(s => $"{s.Id}={s.Status}"
							+ (string.IsNullOrEmpty(s.LastError) ? "" : " — " + s.LastError))));
				}
				else
				{
					// Surface BOTH the message and every sensor's status so
					// state-mismatch bugs (adapter says detached / Core says
					// already attached, etc.) are visible without having to
					// add ad-hoc traces. Sensors list is non-empty whenever
					// Core has a leftover registry from a partial attach.
					var sensorDump = result.Sensors == null || result.Sensors.Count == 0
						? "(none)"
						: string.Join(", ", result.Sensors.Select(s =>
							$"{s.Id}={s.Status}"
							+ (string.IsNullOrEmpty(s.LastError) ? "" : " — " + s.LastError)));
					DiagnosticsLog.Warn($"runtime sensor attach reported failure: {result.Message ?? "(no message)"} | sensors=[{sensorDump}]");
				}
			}
			catch (Exception ex)
			{
				DiagnosticsLog.Error("runtime sensor attach threw", ex);
			}
		}
	}

	public static void Detach()
	{
		lock (_attachLock)
		{
			if (!_attached) return;
			try { RuntimeMonitorBridge.Detach(); }
			catch (Exception ex) { DiagnosticsLog.Warn($"detach threw: {ex.Message}"); }
			_attached = false;
		}
		// Clear the recent ring outside the attach lock to keep that lock cold.
		lock (_writeLock)
		{
			_recent.Clear();
			_snapshot = System.Array.Empty<RuntimeFinding>();
		}
	}

	public static void ReapplySettings()
	{
		// Detach + re-attach so changed flags (Sentinel on/off, allowlist,
		// capture-stack) take effect. EnsureAttached re-reads config.
		Detach();
		EnsureAttached();
	}

	static void OnFindingJson(string json)
	{
		RuntimeFinding f;
		try { f = System.Text.Json.JsonSerializer.Deserialize<RuntimeFinding>(json,
				new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
		catch (Exception ex)
		{
			DiagnosticsLog.Trace($"runtime monitor: unparseable finding json: {ex.Message}");
			return;
		}
		if (f == null) return;

		// Hot path. _writeLock is the SHORT critical section — never held
		// for any other purpose, so the UI thread cannot block on it via
		// any reader (RecentFindings reads the volatile snapshot lock-free).
		// Snapshot is republished on every enqueue; readers always see a
		// consistent array, no half-updated queue.
		lock (_writeLock)
		{
			_recent.Enqueue(f);
			while (_recent.Count > RecentMax) _recent.Dequeue();
			_snapshot = _recent.ToArray();
		}

		try { FindingReceived?.Invoke(f); } catch { }

		// Dedicated audit log for Sentinel kernel events — one line per event,
		// separate from the general secbox.log so the kernel audit trail is
		// trivial to grep / tail / ship to a SIEM. Profiler-only events stay
		// in secbox.log to avoid bloating the audit file with managed noise.
		var sensorIds = f.SensorIds == null ? "(null)" :
			f.SensorIds.Count == 0 ? "(empty)" : string.Join(",", f.SensorIds);
		var hasEtw = f.SensorIds != null && f.SensorIds.Contains("etw");
		DiagnosticsLog.Trace($"sentinel-log-route: sensorIds=[{sensorIds}] hasEtw={hasEtw} kind={f.Kind}");
		if (hasEtw)
		{
			try
			{
				SentinelEventLog.Write(f);
				DiagnosticsLog.Trace($"sentinel-log-route: wrote to {SentinelEventLog.FilePath}");
			}
			catch (Exception ex)
			{
				DiagnosticsLog.Warn($"sentinel-log-route: write failed: {ex.GetType().Name}: {ex.Message}");
			}
		}

		// Severity-based logging — Critical → Error log, others → Info.
		var line = $"[{f.Severity}] {f.Kind} @ {f.Target ?? "(no target)"} "
			+ (string.IsNullOrEmpty(f.CallerAssembly) ? "" : $"by {f.CallerAssembly}::{f.CallerMethod} ")
			+ $"[{string.Join(",", f.SensorIds ?? new List<string>())}]";
		var isCritical = string.Equals(f.Severity, "Critical", StringComparison.OrdinalIgnoreCase);
		if (isCritical)
			DiagnosticsLog.Error("runtime: " + line);
		else if (string.Equals(f.Severity, "High", StringComparison.OrdinalIgnoreCase))
			DiagnosticsLog.Warn("runtime: " + line);
		else
			DiagnosticsLog.Trace("runtime: " + line);

		// Every Critical finding pops the WPF alert dialog. Two paths
		// converge on the same drop folder; whichever spawns first reads
		// + deletes the JSON file, the other becomes a no-op.
		//
		//   Path 1 (service):  drop file → AlertSpawner FileSystemWatcher →
		//                       CreateProcessAsUser → SecboxAlertUI.exe in
		//                       the user's session. Bulletproof against
		//                       editor freezes.
		//   Path 2 (no svc):   drop file → adapter Process.Starts
		//                       SecboxAlertUI.exe directly. Fallback when
		//                       service isn't installed.
		//
		// Prior gating was wrong: it only fired when Tier E had blocked,
		// so kernel-only Critical findings (ETW ProcessStart of an editor
		// descendant, with Tier E's CallAttributionRing entry missed
		// because ETW reported tid=-1) never produced a dialog. Now we
		// always drop on Critical; service or adapter spawns.
		if (isCritical)
		{
			try
			{
				var conf = SecboxConfig.Load();
				if (conf.ShowDetectionDialog)
				{
					TryDropAlertPayload(f);
					if (!conf.SentinelEnabled) TrySpawnAlertUI(f);
				}
			}
			catch (Exception ex)
			{
				DiagnosticsLog.Warn($"detection dialog dispatch failed: {ex.Message}");
			}
		}
	}

	// Drop folder shared by service AlertSpawner + adapter direct-spawn.
	static string AlertDropDir => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
		"secbox", "alerts");

	// Write the alert JSON into the watched drop folder. Atomic rename
	// (.tmp → .json) so the service's FileSystemWatcher never sees a
	// partially-written file. Returns the final path on success, null
	// on any failure (swallowed — audit log already has the finding).
	static string TryDropAlertPayload(RuntimeFinding f)
	{
		try
		{
			Directory.CreateDirectory(AlertDropDir);
			var name = $"editor-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.json";
			var tmp = Path.Combine(AlertDropDir, name + ".tmp");
			var final = Path.Combine(AlertDropDir, name);

			var payload = System.Text.Json.JsonSerializer.Serialize(new
			{
				severity = f.Severity,
				kind = f.Kind,
				target = f.Target,
				callerAssembly = f.CallerAssembly,
				callerMethod = f.CallerMethod,
				timestamp = f.Timestamp,
				pid = f.Pid,
				action = string.Equals(f.Kind, "BlockedManagedProcessStart", StringComparison.Ordinal)
					? "Blocked" : "Detected",
				note = f.Note,
			});
			File.WriteAllText(tmp, payload);
			File.Move(tmp, final);
			return final;
		}
		catch (Exception ex)
		{
			DiagnosticsLog.Warn($"alert payload drop failed: {ex.GetType().Name}: {ex.Message}");
			return null;
		}
	}

	// Adapter-direct spawn path — used when Sentinel service isn't enabled
	// (no AlertSpawner watching). Drops the JSON via TryDropAlertPayload,
	// then Process.Starts SecboxAlertUI.exe directly. Best-effort: any
	// failure (exe missing, .NET Desktop Runtime missing) is swallowed.
	static void TrySpawnAlertUI(RuntimeFinding f)
	{
		try
		{
			var exe = TryFindAlertUiExe();
			if (string.IsNullOrEmpty(exe))
			{
				DiagnosticsLog.Trace("SecboxAlertUI.exe not found in core cache; alert dialog skipped");
				return;
			}

			var payloadPath = TryDropAlertPayload(f);
			if (string.IsNullOrEmpty(payloadPath)) return;

			var psi = new System.Diagnostics.ProcessStartInfo
			{
				FileName = exe,
				UseShellExecute = false,
				CreateNoWindow = false,
				WorkingDirectory = Path.GetDirectoryName(exe),
			};
			psi.ArgumentList.Add(payloadPath);
			System.Diagnostics.Process.Start(psi);
		}
		catch (Exception ex)
		{
			DiagnosticsLog.Warn($"SecboxAlertUI spawn failed: {ex.GetType().Name}: {ex.Message}");
		}
	}

	static string TryFindAlertUiExe()
	{
		try
		{
			// AlertUI ships next to the other Core DLLs (downloaded and
			// hash-verified by SecboxCoreLoader at startup). Resolve from
			// the loaded Core.dll's directory so DevMode / production both
			// work without separate path config.
			var coreAsm = SecboxCoreLoader.CoreAssembly;
			if (coreAsm == null) return null;
			var dir = Path.GetDirectoryName(coreAsm.Location);
			if (string.IsNullOrEmpty(dir)) return null;
			var exe = Path.Combine(dir, "SecboxAlertUI.exe");
			return File.Exists(exe) ? exe : null;
		}
		catch { return null; }
	}
}
