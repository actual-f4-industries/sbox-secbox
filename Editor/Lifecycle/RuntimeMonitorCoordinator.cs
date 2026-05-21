using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sandbox.SecBox.Bridge;
using Sandbox.SecBox.Bridge.Dto;
using DiagnosticsLog = Sandbox.SecBox.Bridge.DiagnosticsLog;

namespace Sandbox.SecBox.Lifecycle;

// Orchestrates Tier E — the in-editor managed-call enforcement hook. Wires the
// event sink to DiagnosticsLog + a small in-memory ring the status panel reads
// for live preview.
//
// Detection tiers were removed: Tier A (Sentinel ETW service + MSI) and Tier B
// (native CLR profiler) are gone. The only sensor is the Harmony hook, which
// suspends the calling thread and shows its OWN blocking decision dialog
// in-process — so the adapter never spawns an AlertUI itself anymore.
//
// Lifecycle:
//   * EnsureAttached — idempotent, called after SecboxCoreClient is ready (boot).
//   * ReapplySettings — call when the user toggles enforcement; detach + reattach.
//   * Detach — call on shutdown / dev-mode reload.
public static class RuntimeMonitorCoordinator
{
	// Locks are split so the event hot path doesn't share a lock with the
	// attach lifecycle or with reader queries.
	//
	//  _attachLock — serialises Attach/Detach/Reapply. Cold path.
	//  _writeLock  — protects the _recent queue. Held briefly during enqueue
	//                + snapshot publish. Held ONLY by event-receiver threads.
	//  _snapshot   — volatile reference, atomic publish on each event.
	//                Readers never take any lock; just read the field.
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
				// Tier E only. Enforcement is gated by BlockLibraryProcessStart;
				// the hook handles the suspend dialog in-process.
				var opts = new RuntimeSensorOptions
				{
					EnableManagedHook = true,
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
					// already attached, etc.) are visible without ad-hoc traces.
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
		// Detach + re-attach so a changed BlockLibraryProcessStart flag takes
		// effect. EnsureAttached re-reads config.
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

		// Hot path. _writeLock is the SHORT critical section — never held for
		// any other purpose, so the UI thread cannot block on it via any reader
		// (RecentFindings reads the volatile snapshot lock-free).
		lock (_writeLock)
		{
			_recent.Enqueue(f);
			while (_recent.Count > RecentMax) _recent.Dequeue();
			_snapshot = _recent.ToArray();
		}

		try { FindingReceived?.Invoke(f); } catch { }

		// Record only — the Tier E hook shows its own blocking decision dialog
		// in-process, so the adapter does not spawn any UI here.
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
	}
}
