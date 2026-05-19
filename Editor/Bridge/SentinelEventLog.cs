using System;
using System.IO;
using System.Text;
using Sandbox.SecBox.Bridge.Dto;

namespace Sandbox.SecBox.Bridge;

// Dedicated audit log for Sentinel kernel events.
// Lives at %LOCALAPPDATA%/secbox/sentinel-events.log.
//
// Separate from secbox.log (which is mixed-purpose diagnostics) so the
// security audit trail is easy to grep, archive, ship to a SIEM, or
// inspect after an incident. Records only events whose SensorIds contain
// "etw" — profiler-only events stay in the main secbox.log.
//
// Same hard invariants as DiagnosticsLog:
//   - Never throws (logging failures swallowed).
//   - Thread-safe via lock; re-opens file each write.
//   - Rotates at MaxBytes (current → .old).
public static class SentinelEventLog
{
	const long MaxBytes = 8 * 1024 * 1024; // 8 MB rotation
	static readonly object _lock = new();
	static bool _firstWrite = true;

	public static string FilePath =>
		Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"secbox", "sentinel-events.log");

	// Writes one event line. Returns silently on any I/O / formatting failure.
	public static void Write(RuntimeFinding f)
	{
		if (f == null) return;
		var line = Format(f);
		WriteRaw(line);
	}

	public static void WriteRaw(string line)
	{
		if (string.IsNullOrEmpty(line)) return;
		lock (_lock)
		{
			try
			{
				var path = FilePath;
				var dir = Path.GetDirectoryName(path);
				if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
					Directory.CreateDirectory(dir);

				RotateIfNeeded(path);

				if (_firstWrite)
				{
					File.AppendAllText(path,
						$"\n\n=== secbox sentinel session start {DateTime.UtcNow:O} pid={Environment.ProcessId} ===\n",
						Encoding.UTF8);
					_firstWrite = false;
				}

				File.AppendAllText(path, line + "\n", Encoding.UTF8);
			}
			catch { /* logger must not throw */ }
		}
	}

	static string Format(RuntimeFinding f)
	{
		// Single-line format optimised for grep + tail.
		// timestamp severity kind target=... pid=... tid=... by=Assembly::Method note=...
		var sb = new StringBuilder(256);
		sb.Append(SafeUtcShort(f.Timestamp)).Append(' ');
		sb.Append('[').Append((f.Severity ?? "?").PadRight(8)).Append("] ");
		sb.Append((f.Kind ?? "Unknown").PadRight(20)).Append(' ');
		sb.Append("target=").Append(Quote(f.Target ?? "(none)")).Append(' ');
		sb.Append("pid=").Append(f.Pid).Append(' ');
		sb.Append("tid=").Append(f.Tid);
		if (!string.IsNullOrEmpty(f.CallerAssembly))
			sb.Append(" by=").Append(f.CallerAssembly).Append("::").Append(f.CallerMethod ?? "?");
		if (!string.IsNullOrEmpty(f.Note))
			sb.Append(" note=").Append(Quote(f.Note));
		if (f.SensorIds != null && f.SensorIds.Count > 0)
			sb.Append(" sensors=[").Append(string.Join(",", f.SensorIds)).Append(']');
		return sb.ToString();
	}

	static string SafeUtcShort(string isoTimestamp)
	{
		if (string.IsNullOrEmpty(isoTimestamp)) return DateTime.UtcNow.ToString("HH:mm:ss.fff");
		if (DateTime.TryParse(isoTimestamp, null,
			System.Globalization.DateTimeStyles.RoundtripKind, out var t))
			return t.ToUniversalTime().ToString("HH:mm:ss.fff");
		return isoTimestamp;
	}

	static string Quote(string s)
	{
		if (s == null) return "\"\"";
		if (s.IndexOf(' ') < 0 && s.IndexOf('"') < 0) return s;
		return "\"" + s.Replace("\"", "\\\"") + "\"";
	}

	static void RotateIfNeeded(string path)
	{
		try
		{
			if (!File.Exists(path)) return;
			var info = new FileInfo(path);
			if (info.Length < MaxBytes) return;

			var oldPath = path + ".old";
			if (File.Exists(oldPath)) File.Delete(oldPath);
			File.Move(path, oldPath);
		}
		catch { /* ignore */ }
	}
}
