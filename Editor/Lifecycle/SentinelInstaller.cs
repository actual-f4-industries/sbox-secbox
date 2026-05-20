using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Sandbox.SecBox.Bridge;
using DiagnosticsLog = Sandbox.SecBox.Bridge.DiagnosticsLog;

namespace Sandbox.SecBox.Lifecycle;

// Manages the lifecycle of the Secbox Sentinel Windows Service:
//   * IsServiceInstalled — query SCM for the service.
//   * IsServiceRunning   — same plus running-state check.
//   * RunInstaller       — launches the MSI with UAC elevation.
//   * RunUninstaller     — same path with /uninstall.
//
// All Win32 calls go through advapi32 P/Invoke rather than
// System.ServiceProcess.ServiceController — the latter needs an assembly
// the s&box engine doesn't ship in the editor's reference set, and we
// can't add a PackageReference because the engine regenerates the csproj
// on save.
//
// Idempotent. Throws nothing on failure; returns a Result struct with a
// human-readable error message so callers can surface it in a dialog.
public static class SentinelInstaller
{
	public const string ServiceName = "SecboxSentinel";
	public const string MsiFileName = "SecboxSentinel.msi";

	// Pinned SHA-256 of SecboxSentinel.msi for the version pinned in
	// CorePolicy.CoreVersion. The MSI is fetched from the same GitHub
	// release as the bridge bundle, hash-verified before launch. Update
	// this whenever CorePolicy.CoreVersion is bumped.
	public const string ExpectedMsiSha256 =
		"f336574723cddfc1065d5ab2717128f58a50a586e5cdece93a8d91707e7a23cc";

	public static string MsiCachePath =>
		Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"secbox", "sentinel", CorePolicy.CoreVersion, MsiFileName);

	public static string MsiDownloadUrl =>
		$"{CorePolicy.BaseUrl.TrimEnd('/')}/{CorePolicy.CoreVersion}/{MsiFileName}";

	public static bool IsServiceInstalled()
	{
		var scm = OpenSCManagerSafe();
		if (scm == IntPtr.Zero) return false;
		try
		{
			var svc = OpenService(scm, ServiceName, SERVICE_QUERY_STATUS);
			if (svc == IntPtr.Zero) return false;
			CloseServiceHandle(svc);
			return true;
		}
		finally { CloseServiceHandle(scm); }
	}

	public static bool IsServiceRunning()
	{
		var scm = OpenSCManagerSafe();
		if (scm == IntPtr.Zero) return false;
		try
		{
			var svc = OpenService(scm, ServiceName, SERVICE_QUERY_STATUS);
			if (svc == IntPtr.Zero) return false;
			try
			{
				if (!QueryServiceStatus(svc, out var status)) return false;
				return status.dwCurrentState == SERVICE_RUNNING;
			}
			finally { CloseServiceHandle(svc); }
		}
		finally { CloseServiceHandle(scm); }
	}

	// Fetches the MSI from GitHub Releases and SHA-256-verifies it. Cache
	// hit on subsequent calls. Same trust model as SecboxCoreLoader: refuses
	// to write a file whose hash doesn't match ExpectedMsiSha256.
	public static async Task<Result> EnsureMsiCachedAsync(CancellationToken ct = default)
	{
		try
		{
			var path = MsiCachePath;
			if (File.Exists(path))
			{
				var existing = await Sha256OfFileAsync(path, ct).ConfigureAwait(false);
				if (string.Equals(existing, ExpectedMsiSha256, StringComparison.OrdinalIgnoreCase))
				{
					DiagnosticsLog.Trace($"sentinel MSI cache hit at {path}");
					return Result.Ok(path);
				}
				DiagnosticsLog.Warn($"sentinel MSI cache hash mismatch at {path}; re-downloading");
				try { File.Delete(path); } catch { }
			}

			Directory.CreateDirectory(Path.GetDirectoryName(path)!);

			using var http = new HttpClient();
			http.DefaultRequestHeaders.UserAgent.ParseAdd($"secbox-adapter/{CorePolicy.CoreVersion}");
			http.Timeout = TimeSpan.FromMinutes(3);

			DiagnosticsLog.Info($"downloading sentinel MSI from {MsiDownloadUrl}");
			byte[] bytes;
			try { bytes = await http.GetByteArrayAsync(MsiDownloadUrl, ct).ConfigureAwait(false); }
			catch (Exception ex) { return Result.Fail($"MSI download failed: {ex.Message}"); }

			var actual = Sha256OfBytes(bytes);
			if (!string.Equals(actual, ExpectedMsiSha256, StringComparison.OrdinalIgnoreCase))
			{
				DiagnosticsLog.Error($"sentinel MSI hash mismatch: expected {ExpectedMsiSha256}, got {actual}");
				return Result.Fail(
					$"MSI hash mismatch. Expected {ExpectedMsiSha256[..12]}…, got {actual[..12]}…. " +
					"Refusing to write or launch — possible tampering or stale adapter pin.");
			}

			await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
			DiagnosticsLog.Info($"sentinel MSI cached ({bytes.Length:N0} bytes) at {path}");
			return Result.Ok(path);
		}
		catch (Exception ex)
		{
			return Result.Fail($"MSI download/verify failed: {ex.Message}");
		}
	}

	// Path of the verbose msiexec log emitted by every RunInstaller /
	// RunUninstaller invocation. Stamped per-call so multiple attempts
	// don't trample each other.
	public static string MsiLogPath(string action) =>
		Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"secbox", "sentinel",
			$"msi-{action}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");

	// Launches msiexec on a cached MSI. If `msiPath` is null and the MSI
	// isn't in the cache, the caller should have already invoked
	// EnsureMsiCachedAsync — this method does not download.
	public static Result RunInstaller(string msiPath = null)
	{
		msiPath ??= MsiCachePath;
		if (!File.Exists(msiPath))
			return Result.Fail($"MSI not found at {msiPath}. Run EnsureMsiCachedAsync first.");

		var logPath = MsiLogPath("install");
		try { Directory.CreateDirectory(Path.GetDirectoryName(logPath)!); } catch { }

		try
		{
			var psi = new ProcessStartInfo
			{
				FileName = "msiexec.exe",
				// Full UI mode — no /q flag — so the WixUI_Minimal dialog
				// renders the branded banner and welcome bitmap. /l*v still
				// writes the verbose log alongside the visible install
				// dialogs (essential for diagnosing 1603 and other generic-
				// failure exit codes).
				Arguments = $"/i \"{msiPath}\" /l*v \"{logPath}\"",
				UseShellExecute = true,
				Verb = "runas", // UAC prompt
			};
			var proc = Process.Start(psi);
			if (proc == null) return Result.Fail("msiexec did not start.");
			proc.WaitForExit();

			if (proc.ExitCode == 0)
			{
				return Result.Ok(
					$"Installer exited cleanly. Service installed: {IsServiceInstalled()}, running: {IsServiceRunning()}. Log: {logPath}");
			}

			var detail = ExtractLastErrorFromMsiLog(logPath);
			return Result.Fail(
				$"Installer exit code {proc.ExitCode} ({ExplainExitCode(proc.ExitCode)}). " +
				(detail != null ? $"Detail: {detail}. " : "") +
				$"Full log: {logPath}");
		}
		catch (Exception ex)
		{
			return Result.Fail($"Failed to launch installer: {ex.Message}");
		}
	}

	// Best-effort: walk the MSI verbose log backwards from the end, find
	// the last line containing a recognisable failure marker, return a
	// trimmed substring. Reading thousands of lines is fine — the log is
	// only generated when an install is in progress.
	static string ExtractLastErrorFromMsiLog(string logPath)
	{
		try
		{
			if (!File.Exists(logPath)) return null;
			var lines = File.ReadAllLines(logPath);
			for (int i = lines.Length - 1; i >= 0 && i >= lines.Length - 400; i--)
			{
				var line = lines[i];
				if (line.Length == 0) continue;
				// Highest signal: explicit MSI error rows.
				if (line.Contains("Error 1920") || line.Contains("Error 1921")
					|| line.Contains("Error 1923") || line.Contains("Error 1924"))
					return Trim(line);
				if (line.Contains("Note: 1: 1920") || line.Contains("Note: 1: 1921"))
					return Trim(line);
				if (line.Contains("CustomAction") && line.Contains("returned actual error code"))
					return Trim(line);
				if (line.Contains("returnValue 3") && line.Contains("MainEngineThread"))
					return Trim(line);
				if (line.Contains("Installation failed."))
					return Trim(line);
			}
			// Fallback: last non-empty line.
			for (int i = lines.Length - 1; i >= 0; i--)
				if (lines[i].Length > 0) return Trim(lines[i]);
			return null;
		}
		catch { return null; }

		static string Trim(string s) => s.Length > 220 ? s.Substring(0, 220) + "…" : s;
	}

	static string ExplainExitCode(int code) => code switch
	{
		1602 => "user cancelled",
		1603 => "fatal install error — check log",
		1605 => "this action is only valid for installed products",
		1612 => "installation source unavailable",
		1618 => "another install in progress",
		1619 => "MSI couldn't be opened — corrupt or wrong perms",
		1625 => "blocked by group policy",
		1633 => "platform not supported (architecture mismatch?)",
		1638 => "another version already installed — uninstall first",
		_    => "see https://learn.microsoft.com/windows/win32/msi/error-codes",
	};

	// Convenience: download (if needed) + verify + launch in one call.
	// Suitable for UI buttons that want a single "do it" entry point.
	public static async Task<Result> DownloadAndInstallAsync(CancellationToken ct = default)
	{
		var dl = await EnsureMsiCachedAsync(ct).ConfigureAwait(false);
		if (!dl.Success) return dl;
		return RunInstaller(dl.Message); // Result.Ok carries the cache path
	}

	static async Task<string> Sha256OfFileAsync(string path, CancellationToken ct)
	{
		using var sha = SHA256.Create();
		using var fs = File.OpenRead(path);
		var hash = await Task.Run(() => sha.ComputeHash(fs), ct).ConfigureAwait(false);
		return ToHex(hash);
	}

	static string Sha256OfBytes(byte[] bytes)
	{
		using var sha = SHA256.Create();
		return ToHex(sha.ComputeHash(bytes));
	}

	static string ToHex(byte[] bytes)
	{
		var sb = new System.Text.StringBuilder(bytes.Length * 2);
		for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
		return sb.ToString();
	}

	public static Result RunUninstaller(string msiPath = null)
	{
		msiPath ??= MsiCachePath;
		var logPath = MsiLogPath("uninstall");
		try { Directory.CreateDirectory(Path.GetDirectoryName(logPath)!); } catch { }

		try
		{
			var arg = File.Exists(msiPath)
				? $"/x \"{msiPath}\""
				: "/x {d8a64db3-9a3f-4b1f-9d80-1b87f4d3a601}"; // ProductCode
			var psi = new ProcessStartInfo
			{
				FileName = "msiexec.exe",
				// Full UI to match the install flow's branded experience.
				Arguments = $"{arg} /l*v \"{logPath}\"",
				UseShellExecute = true,
				Verb = "runas",
			};
			var proc = Process.Start(psi);
			if (proc == null) return Result.Fail("msiexec did not start.");
			proc.WaitForExit();

			if (proc.ExitCode == 0)
				return Result.Ok($"Uninstaller exited cleanly. Log: {logPath}");

			var detail = ExtractLastErrorFromMsiLog(logPath);
			return Result.Fail(
				$"Uninstaller exit code {proc.ExitCode} ({ExplainExitCode(proc.ExitCode)}). " +
				(detail != null ? $"Detail: {detail}. " : "") +
				$"Full log: {logPath}");
		}
		catch (Exception ex)
		{
			return Result.Fail($"Failed to launch uninstaller: {ex.Message}");
		}
	}

	public static Result TryStartService()
	{
		var scm = OpenSCManagerSafe();
		if (scm == IntPtr.Zero) return Result.Fail("Could not open SCM (need elevation?).");
		try
		{
			var svc = OpenService(scm, ServiceName, SERVICE_QUERY_STATUS | SERVICE_START);
			if (svc == IntPtr.Zero) return Result.Fail($"Could not open service (win32={Marshal.GetLastWin32Error()}).");
			try
			{
				if (!QueryServiceStatus(svc, out var status))
					return Result.Fail($"QueryServiceStatus failed (win32={Marshal.GetLastWin32Error()}).");
				if (status.dwCurrentState == SERVICE_RUNNING) return Result.Ok("Already running.");
				if (!StartService(svc, 0, IntPtr.Zero))
					return Result.Fail($"StartService failed (win32={Marshal.GetLastWin32Error()}). Likely needs admin.");
				return Result.Ok("Service start requested.");
			}
			finally { CloseServiceHandle(svc); }
		}
		finally { CloseServiceHandle(scm); }
	}

	public static Result TryStopService()
	{
		var scm = OpenSCManagerSafe();
		if (scm == IntPtr.Zero) return Result.Fail("Could not open SCM (need elevation?).");
		try
		{
			var svc = OpenService(scm, ServiceName, SERVICE_QUERY_STATUS | SERVICE_STOP);
			if (svc == IntPtr.Zero) return Result.Fail($"Could not open service (win32={Marshal.GetLastWin32Error()}).");
			try
			{
				if (!QueryServiceStatus(svc, out var status))
					return Result.Fail($"QueryServiceStatus failed (win32={Marshal.GetLastWin32Error()}).");
				if (status.dwCurrentState == SERVICE_STOPPED) return Result.Ok("Already stopped.");
				if (!ControlService(svc, SERVICE_CONTROL_STOP, out _))
					return Result.Fail($"ControlService(STOP) failed (win32={Marshal.GetLastWin32Error()}). Likely needs admin.");
				return Result.Ok("Service stop requested.");
			}
			finally { CloseServiceHandle(svc); }
		}
		finally { CloseServiceHandle(scm); }
	}

	public readonly struct Result
	{
		public bool Success { get; }
		public string Message { get; }
		Result(bool ok, string msg) { Success = ok; Message = msg; }
		public static Result Ok(string m) => new(true, m);
		public static Result Fail(string m) => new(false, m);
	}

	// ----- Win32 P/Invoke (advapi32) -----
	// Replaces System.ServiceProcess.ServiceController, which lives in an
	// assembly the s&box editor doesn't ship.

	const uint SC_MANAGER_CONNECT = 0x0001;
	const uint SERVICE_QUERY_STATUS = 0x0004;
	const uint SERVICE_START = 0x0010;
	const uint SERVICE_STOP = 0x0020;
	const uint SERVICE_CONTROL_STOP = 0x00000001;

	const uint SERVICE_STOPPED = 0x00000001;
	const uint SERVICE_RUNNING = 0x00000004;

	[StructLayout(LayoutKind.Sequential)]
	struct SERVICE_STATUS
	{
		public uint dwServiceType;
		public uint dwCurrentState;
		public uint dwControlsAccepted;
		public uint dwWin32ExitCode;
		public uint dwServiceSpecificExitCode;
		public uint dwCheckPoint;
		public uint dwWaitHint;
	}

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenSCManagerW")]
	static extern IntPtr OpenSCManager(string machineName, string databaseName, uint access);

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenServiceW")]
	static extern IntPtr OpenService(IntPtr scm, string serviceName, uint access);

	[DllImport("advapi32.dll", SetLastError = true)]
	static extern bool QueryServiceStatus(IntPtr service, out SERVICE_STATUS status);

	[DllImport("advapi32.dll", SetLastError = true)]
	static extern bool CloseServiceHandle(IntPtr handle);

	[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "StartServiceW")]
	static extern bool StartService(IntPtr service, uint numArgs, IntPtr args);

	[DllImport("advapi32.dll", SetLastError = true)]
	static extern bool ControlService(IntPtr service, uint control, out SERVICE_STATUS status);

	static IntPtr OpenSCManagerSafe()
	{
		try { return OpenSCManager(null, null, SC_MANAGER_CONNECT); }
		catch (Exception ex)
		{
			DiagnosticsLog.Warn($"OpenSCManager threw: {ex.Message}");
			return IntPtr.Zero;
		}
	}
}
