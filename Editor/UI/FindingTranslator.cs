using System;
using System.Collections.Generic;

namespace Sandbox.SecBox.UI;

// Plain-language dictionary for the Default tab of the review window.
// Lookup is exact-match on the full RuleId first, then prefix match
// (rules like critical.interop.001/002/003 share a single explanation).
public static class FindingTranslator
{
	public readonly record struct Explanation(string Title, string Plain);

	static readonly Dictionary<string, Explanation> Exact = new(StringComparer.OrdinalIgnoreCase)
	{
		["native.unmanaged-dll"] = new(
			"Ships a native Windows DLL",
			"This package contains a native Windows library. The scanner cannot read machine code - once loaded, it can do anything on your machine."),
		["native.unix-shared-object"] = new(
			"Ships a native Unix library",
			"This package contains a Linux or macOS native library (.so / .dylib). The scanner cannot read machine code."),
		["native.executable"] = new(
			"Ships a standalone executable",
			"This package contains a runnable program (.exe, .bat, .ps1, .sh, .cmd). Managed library packages should not ship executables."),

		["source.suspect-using"] = new(
			"Imports a risky namespace",
			"A source file imports a namespace commonly used for risky operations. On its own this is just a hint - check what the code does with it."),
		["source.critical-attr"] = new(
			"Uses a dangerous attribute",
			"A source file applies an attribute such as [DllImport] that signals a clear attack-style pattern."),
		["source.critical-ident"] = new(
			"Names a dangerous type",
			"A source file references a dangerous type by name (Process, AssemblyLoadContext, NativeLibrary, …). Strong attack signal."),
		["source.high-ident"] = new(
			"Names a risky type",
			"A source file references a risky type (File, Socket, Registry, …). Confirm whether it is actually used in the compiled binary."),
		["source.suspicious-literal"] = new(
			"Suspicious string in source",
			"A source file contains a string matching the name of a native API or shell command (kernel32, powershell, cmd.exe, …)."),
		["source.read-failed"] = new(
			"Could not read a source file",
			"The scanner failed to open a source file. This is a scanner error, not necessarily a concern."),
		["source.parse-failed"] = new(
			"Could not parse a source file",
			"The scanner failed to parse a file as C#. The file may be invalid or use unsupported syntax."),

		["il.pinned-local"] = new(
			"Pinned memory in compiled code",
			"The compiled code pins memory in place. Usually paired with pointer arithmetic, this allows reading and writing outside normal .NET safety."),
		["il.suspicious-literal"] = new(
			"Suspicious string in compiled code",
			"The compiled code embeds a string matching the name of a native API or shell command."),
		["il.finalizer-trick"] = new(
			"Finalizer-reference exploit",
			"The compiled code references object finalizers indirectly via ldftn / ldvirtftn - a known .NET sandbox-escape technique."),
		["il.read-failed"] = new(
			"Could not read an assembly",
			"The scanner failed to load a compiled .dll for IL analysis."),

		["metadata.pinvoke"] = new(
			"Declares a P/Invoke",
			"The compiled code declares a direct call into native code via P/Invoke. This is one of the strongest possible attack signals."),
		["metadata.explicit-layout"] = new(
			"Memory-aliasing type",
			"The code defines a type with explicit memory layout. Often used to reinterpret bytes as another type - a building block for memory exploits."),
		["metadata.read-failed"] = new(
			"Could not read assembly metadata",
			"The scanner could not parse the assembly's metadata tables."),

		["engine.not-whitelisted"] = new(
			"Calls an API outside the engine allowlist",
			"The code calls a .NET API that the s&box engine does not normally permit for game-side code."),
		["engine.foreign-assembly"] = new(
			"References an unknown assembly",
			"The code references an assembly that is not part of the s&box engine or its game-side surface."),

		["pipeline.finder-threw"] = new(
			"A scanner step failed",
			"One of the scanner steps threw an exception while inspecting this package. Treat as a scanner error."),
		["core.scan-error"] = new(
			"Scan failed",
			"The overall scan failed with an error."),
	};

	static readonly (string Prefix, Explanation Entry)[] Prefixes =
	{
		("critical.interop.", new(
			"Calls native code directly",
			"This library calls into native code (Windows DLLs, Linux .so files). Native code runs outside the .NET sandbox and can do anything on the machine.")),
		("critical.process.", new(
			"Launches other programs",
			"This library can start other programs on your machine (cmd.exe, PowerShell, anything on PATH).")),
		("critical.dynamic-code.", new(
			"Loads or compiles code at runtime",
			"This library loads assemblies, emits IL, or compiles scripts at runtime. What the code does cannot be seen by a static scan.")),
		("critical.raw-network.", new(
			"Direct network access",
			"This library opens raw network sockets or makes web requests outside the engine's sandboxed networking.")),
		("critical.filesystem.", new(
			"Direct file access",
			"This library reads and writes files outside the engine's sandboxed filesystem (Sandbox.FileSystem).")),
		("critical.reflection.", new(
			"Inspects or invokes code by name",
			"This library uses reflection to call code by name. Often used to reach private code or bypass restrictions.")),
		("critical.environment.", new(
			"Reads system / environment info",
			"This library reads environment variables, the registry, or other machine info (username, machine name, …).")),
	};

	public static Explanation Translate(string ruleId)
	{
		if (string.IsNullOrEmpty(ruleId))
			return new("Unrecognised finding", "The scanner produced a finding without a rule identifier.");

		if (Exact.TryGetValue(ruleId, out var exact))
			return exact;

		foreach (var (prefix, entry) in Prefixes)
			if (ruleId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				return entry;

		return new(ruleId, "No plain-language explanation is available for this rule yet. See the Advanced tab for technical details.");
	}
}
