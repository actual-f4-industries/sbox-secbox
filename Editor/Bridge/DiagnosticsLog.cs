using System;
using System.IO;
using System.Text;

namespace Sandbox.SecBox.Bridge;

// File-backed diagnostic log at %LOCALAPPDATA%/secbox/secbox.log.
//
// Why: when secbox hangs or breaks the editor, the engine's in-memory log
// disappears with the editor process. A file persists across crashes and
// can be inspected after the fact. This logger also mirrors to the engine's
// Log when reachable, so live tailing works in normal operation.
//
// Hard invariants:
//   - Never throws. Logging failures are swallowed — the LAST thing we want
//     is the logger crashing inside an exception handler.
//   - Thread-safe via lock. We re-open the file each write so concurrent
//     processes (CLI + editor running side-by-side) don't corrupt it.
//   - Rotates at MaxBytes — current → .old, fresh file started.
public static class DiagnosticsLog
{
	const long MaxBytes = 4 * 1024 * 1024; // 4 MB rotation
	static readonly object _lock = new();
	static bool _firstWrite = true;

	public static string FilePath =>
		Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"secbox", "secbox.log");

	public static void Trace(string message)                  => Write("TRACE", message, null);
	public static void Info(string message)                   => Write("INFO ", message, null);
	public static void Warn(string message)                   => Write("WARN ", message, null);
	public static void Error(string message)                  => Write("ERROR", message, null);
	public static void Error(string message, Exception ex)    => Write("ERROR", message, ex);
	public static void Fatal(string message, Exception ex)    => Write("FATAL", message, ex);

	// One-line trace + automatic exception capture if `func` throws. Returns
	// the exception if thrown (else null) so callers can re-throw if needed.
	public static Exception Wrap(string label, Action func)
	{
		Trace($"BEGIN {label}");
		try { func(); Trace($"END   {label}"); return null; }
		catch (Exception ex)
		{
			Error($"FAIL  {label}", ex);
			return ex;
		}
	}

	static void Write(string level, string message, Exception ex)
	{
		var line = BuildLine(level, message, ex);

		// Mirror to engine log when we can (live tail in editor).
		try
		{
			if (level == "ERROR" || level == "FATAL")
				global::Sandbox.Internal.GlobalGameNamespace.Log.Error(line);
			else if (level == "WARN ")
				global::Sandbox.Internal.GlobalGameNamespace.Log.Warning(line);
			else
				global::Sandbox.Internal.GlobalGameNamespace.Log.Info(line);
		}
		catch { /* logger must not throw */ }

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
						$"\n\n=== secbox session start {DateTime.UtcNow:O} pid={Environment.ProcessId} ===\n",
						Encoding.UTF8);
					_firstWrite = false;
				}

				File.AppendAllText(path, line + "\n", Encoding.UTF8);
			}
			catch { /* logger must not throw */ }
		}
	}

	static string BuildLine(string level, string message, Exception ex)
	{
		var sb = new StringBuilder();
		sb.Append(DateTime.UtcNow.ToString("HH:mm:ss.fff"));
		sb.Append(' ').Append(level).Append(' ');
		sb.Append('[').Append(Environment.CurrentManagedThreadId.ToString("D2")).Append("] ");
		sb.Append(message);
		if (ex != null)
		{
			sb.Append('\n').Append(ex.GetType().FullName).Append(": ").Append(ex.Message);
			if (!string.IsNullOrEmpty(ex.StackTrace))
				sb.Append('\n').Append(ex.StackTrace);
			var inner = ex.InnerException;
			while (inner != null)
			{
				sb.Append("\n--- inner: ").Append(inner.GetType().FullName).Append(": ").Append(inner.Message);
				if (!string.IsNullOrEmpty(inner.StackTrace)) sb.Append('\n').Append(inner.StackTrace);
				inner = inner.InnerException;
			}
		}
		return sb.ToString();
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

	// Installs an AppDomain.UnhandledException handler so anything we miss
	// in our explicit try/catch still ends up on disk. Idempotent.
	//
	// FirstChanceException tracing is ONLY enabled when verbose=true. The
	// engine throws + catches many internal exceptions per second (asset
	// serialization probes, Cecil resolution attempts, etc.) — tracing all
	// of them floods the log and slows secbox itself.
	static bool _hookedUnhandled;
	static bool _hookedFirstChance;
	public static void InstallUnhandledHandler(bool verbose = false)
	{
		if (!_hookedUnhandled)
		{
			_hookedUnhandled = true;
			try
			{
				AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
				{
					Fatal("AppDomain.UnhandledException", args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString() ?? "(non-exception)"));
				};
			}
			catch { }
		}

		if (verbose && !_hookedFirstChance)
		{
			_hookedFirstChance = true;
			try
			{
				AppDomain.CurrentDomain.FirstChanceException += (sender, args) =>
				{
					var msg = args.Exception?.Message ?? "(null)";
					if (msg.Length > 200) msg = msg[..200] + "…";
					Trace($"first-chance: {args.Exception?.GetType().Name}: {msg}");
				};
			}
			catch { }
		}
	}
}
