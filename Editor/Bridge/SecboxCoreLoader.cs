using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Sandbox.SecBox.Bridge;

// Downloads + SHA-256-verifies + AssemblyLoadContext-loads Secbox.Core.dll
// (and its dependency DLLs). Caches under CorePolicy.LocalCachePath so we
// only network once per version. Refuses to load any blob whose SHA-256
// doesn't match the value pinned in CorePolicy.
//
// Dev override: setting %SECBOX_DEV_PATH% to a folder skips download/verify
// and loads directly from that path. Intended for local iteration only.
public static class SecboxCoreLoader
{
	const string LoadContextName = "secbox-core";
	static Assembly _coreAssembly;
	static AssemblyLoadContext _alc;

	public static Assembly CoreAssembly => _coreAssembly;

	public static async Task<Assembly> EnsureLoadedAsync()
	{
		if (_coreAssembly != null) return _coreAssembly;

		DiagnosticsLog.Trace("SecboxCoreLoader.EnsureLoadedAsync: begin");

		string folder;
		var dev = CorePolicy.DevOverridePath;
		if (!string.IsNullOrEmpty(dev) && Directory.Exists(dev))
		{
			DiagnosticsLog.Warn($"DEV MODE: loading core from {dev} (hash verification skipped)");
			folder = dev;
		}
		else
		{
			folder = CorePolicy.LocalCachePath;
			DiagnosticsLog.Trace($"production load path; cache folder = {folder}");
			try
			{
				await EnsureCachedAsync(folder);
			}
			catch (Exception ex)
			{
				DiagnosticsLog.Error("EnsureCachedAsync failed", ex);
				throw;
			}
		}

		try
		{
			_alc = new AssemblyLoadContext(LoadContextName, isCollectible: true);
			_alc.Resolving += (ctx, name) =>
			{
				var candidate = Path.Combine(folder, name.Name + ".dll");
				DiagnosticsLog.Trace($"ALC.Resolving: {name.Name} → {(File.Exists(candidate) ? "found" : "missing")} ({candidate})");
				// 0Harmony (+ bundled MonoMod) MUST be a SINGLE instance in the
				// Default, non-collectible context. If this collectible ALC loads
				// its own copy, Core binds to it while Harmony's MonoMod detours
				// (emitted into Default) bind to a separate Default copy - the two
				// type identities collide and every patch throws "CecilILGenerator
				// … violates the constraint of TTarget" (0 methods patched). Hand
				// 0Harmony/MonoMod to Default so Core + the detours share one copy.
				var shared = name.Name ?? "";
				bool toDefault = File.Exists(candidate) && (string.Equals(shared, "0Harmony", StringComparison.OrdinalIgnoreCase) || shared.StartsWith("MonoMod", StringComparison.OrdinalIgnoreCase));
				if (toDefault)
				{
					DiagnosticsLog.Trace($"ALC.Resolving: {shared} → Default ALC (shared single-instance)");
					return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
				}
				return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
			};

			var corePath = Path.Combine(folder, "Secbox.Core.dll");
			if (!File.Exists(corePath))
			{
				DiagnosticsLog.Error($"Secbox.Core.dll missing at {corePath}");
				throw new FileNotFoundException("Secbox.Core.dll not found.", corePath);
			}

			DiagnosticsLog.Trace($"loading Secbox.Core from {corePath}");
			_coreAssembly = _alc.LoadFromAssemblyPath(corePath);
			DiagnosticsLog.Info($"loaded {_coreAssembly.GetName().Name} v{_coreAssembly.GetName().Version}");
			return _coreAssembly;
		}
		catch (Exception ex)
		{
			DiagnosticsLog.Error("ALC load failed", ex);
			throw;
		}
	}

	// Unload the ALC so a fresh Secbox.Core can be loaded after an update.
	// Returns true if collection was triggered; the unload happens
	// asynchronously and only completes when no managed references remain.
	public static bool TryUnload()
	{
		if (_alc == null) return false;
		_alc.Unload();
		_alc = null;
		_coreAssembly = null;
		GC.Collect();
		GC.WaitForPendingFinalizers();
		return true;
	}

	static async Task EnsureCachedAsync(string folder)
	{
		Directory.CreateDirectory(folder);

		using var http = new HttpClient();
		http.DefaultRequestHeaders.UserAgent.ParseAdd($"secbox-adapter/{CorePolicy.CoreVersion}");
		http.Timeout = TimeSpan.FromSeconds(60); // refuse to hang on slow CDN

		foreach (var (fileName, expectedHash) in CorePolicy.CoreFiles)
		{
			var destPath = Path.Combine(folder, fileName);

			if (File.Exists(destPath) && Sha256OfFile(destPath) == expectedHash)
			{
				DiagnosticsLog.Trace($"cache hit for {fileName}");
				continue;
			}

			if (!CorePolicy.AutoUpdate)
			{
				DiagnosticsLog.Error($"cached {fileName} missing or hash mismatch, AutoUpdate off");
				throw new InvalidOperationException(
					$"Cached {fileName} missing or hash mismatch, and AutoUpdate is off.");
			}

			var url = CorePolicy.BaseUrl.TrimEnd('/') + "/" + CorePolicy.CoreVersion + "/" + fileName;
			DiagnosticsLog.Info($"downloading {fileName} from {url}");

			byte[] bytes;
			try
			{
				bytes = await http.GetByteArrayAsync(url);
			}
			catch (Exception ex)
			{
				DiagnosticsLog.Error($"download failed for {fileName}", ex);
				throw new InvalidOperationException(
					$"Failed to download {fileName}: {ex.Message}", ex);
			}

			var actualHash = Sha256OfBytes(bytes);
			if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
			{
				DiagnosticsLog.Error($"SHA-256 mismatch for {fileName}: expected {expectedHash} got {actualHash}");
				throw new InvalidOperationException(
					$"SHA-256 mismatch for {fileName}. Expected {expectedHash}, got {actualHash}. "
					+ $"Refusing to load - possible tampering or stale adapter.");
			}

			await File.WriteAllBytesAsync(destPath, bytes);
			DiagnosticsLog.Trace($"wrote verified {fileName} ({bytes.Length} bytes)");
		}
	}

	static string Sha256OfFile(string path)
	{
		using var sha = SHA256.Create();
		using var fs = File.OpenRead(path);
		return ToHex(sha.ComputeHash(fs));
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
}
