using System;
using System.Collections.Generic;
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
	static readonly object _lock = new();
	static bool _attached;
	static readonly Queue<RuntimeFinding> _recent = new();
	const int RecentMax = 256;

	public static bool IsAttached { get { lock (_lock) return _attached; } }

	public static IReadOnlyList<RuntimeFinding> RecentFindings
	{
		get { lock (_lock) return _recent.ToArray(); }
	}

	public static int RecentCount { get { lock (_lock) return _recent.Count; } }

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
		lock (_lock)
		{
			if (_attached) return;
			try
			{
				var opts = new RuntimeSensorOptions
				{
					EnableProfiler = true,
					EnableEtw = cfg.SentinelEnabled,
					CaptureStack = cfg.CaptureStackOnKernelEvents,
					PathAllowlist = cfg.SentinelPathAllowlist?.Count > 0
						? cfg.SentinelPathAllowlist : null,
				};
				var result = RuntimeMonitorBridge.Attach(opts, OnFindingJson);
				_attached = result.Attached;

				if (result.Attached)
				{
					DiagnosticsLog.Info($"runtime sensors attached: "
						+ string.Join(", ", result.Sensors.Select(s => $"{s.Id}={s.Status}")));
				}
				else
				{
					DiagnosticsLog.Warn($"runtime sensor attach reported failure: {result.Message}");
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
		lock (_lock)
		{
			if (!_attached) return;
			try { RuntimeMonitorBridge.Detach(); }
			catch (Exception ex) { DiagnosticsLog.Warn($"detach threw: {ex.Message}"); }
			_attached = false;
			_recent.Clear();
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

		lock (_lock)
		{
			_recent.Enqueue(f);
			while (_recent.Count > RecentMax) _recent.Dequeue();
		}

		try { FindingReceived?.Invoke(f); } catch { }

		// Severity-based logging — Critical → Error log, others → Info.
		var line = $"[{f.Severity}] {f.Kind} @ {f.Target ?? "(no target)"} "
			+ (string.IsNullOrEmpty(f.CallerAssembly) ? "" : $"by {f.CallerAssembly}::{f.CallerMethod} ")
			+ $"[{string.Join(",", f.SensorIds ?? new List<string>())}]";
		if (string.Equals(f.Severity, "Critical", StringComparison.OrdinalIgnoreCase))
			DiagnosticsLog.Error("runtime: " + line);
		else if (string.Equals(f.Severity, "High", StringComparison.OrdinalIgnoreCase))
			DiagnosticsLog.Warn("runtime: " + line);
		else
			DiagnosticsLog.Trace("runtime: " + line);
	}
}
