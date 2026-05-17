using System.Collections.Generic;

namespace Sandbox.SecBox.Rules;

// Patterns that ALWAYS produce a Critical finding when seen, regardless of
// assembly whitelist. These are the smoking-gun APIs no legitimate editor tool
// needs without extraordinary justification.
//
// Rule format: assembly-agnostic — patterns use "*/Member" so they match any
// assembly that exposes a member with the given path. This guards against an
// attacker shipping a forwarder type from a custom assembly to evade
// AssemblyName-anchored rules.
public static class CriticalRules
{
	// P/Invoke and native interop — direct calls into native code.
	public static readonly string[] Interop = new[]
	{
		"*/System.Runtime.InteropServices.DllImportAttribute*",
		"*/System.Runtime.InteropServices.LibraryImportAttribute*",
		"*/System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer*",
		"*/System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate*",
		"*/System.Runtime.InteropServices.Marshal.AllocHGlobal*",
		"*/System.Runtime.InteropServices.Marshal.Copy*",
		"*/System.Runtime.InteropServices.Marshal.PtrToStructure*",
		"*/System.Runtime.InteropServices.Marshal.StructureToPtr*",
		"*/System.Runtime.InteropServices.Marshal.WriteByte*",
		"*/System.Runtime.InteropServices.Marshal.WriteInt32*",
		"*/System.Runtime.InteropServices.Marshal.WriteIntPtr*",
		"*/System.Runtime.InteropServices.NativeLibrary*",
		"*/System.Runtime.InteropServices.SuppressGCTransitionAttribute*",
		"*/System.Runtime.CompilerServices.UnsafeAccessorAttribute*",
	};

	// Process spawning.
	public static readonly string[] Process = new[]
	{
		"*/System.Diagnostics.Process*",
		"*/System.Diagnostics.ProcessStartInfo*",
	};

	// Dynamic assembly load + IL emit + script execution.
	public static readonly string[] DynamicCode = new[]
	{
		"*/System.Reflection.Assembly.Load*",
		"*/System.Reflection.Assembly.LoadFile*",
		"*/System.Reflection.Assembly.LoadFrom*",
		"*/System.Reflection.Assembly.UnsafeLoadFrom*",
		"*/System.Runtime.Loader.AssemblyLoadContext*",
		"*/System.Reflection.Emit.*",
		"*/Microsoft.CodeAnalysis.CSharp.Scripting.*",
		"*/Microsoft.CodeAnalysis.Scripting.*",
		"*/System.Linq.Expressions.Expression.Compile*",
	};

	// Raw networking. HTTPS via System.Net.Http is engine-whitelisted; raw
	// sockets, web requests, listeners are not.
	public static readonly string[] RawNetwork = new[]
	{
		"*/System.Net.Sockets.*",
		"*/System.Net.NetworkInformation.*",
		"*/System.Net.HttpListener*",
		"*/System.Net.WebClient*",
		"*/System.Net.WebRequest*",
		"*/System.Net.FtpWebRequest*",
		"*/System.Net.Dns.*",
	};

	// Direct BCL filesystem access. Engine ships Sandbox.Filesystem for
	// sandboxed I/O; any raw System.IO.File/Directory use in an editor library
	// is a strong signal of unwanted host access.
	public static readonly string[] Filesystem = new[]
	{
		"*/System.IO.File.*",
		"*/System.IO.Directory.*",
		"*/System.IO.DirectoryInfo*",
		"*/System.IO.FileInfo*",
		"*/System.IO.FileStream*",
		"*/System.IO.FileSystemWatcher*",
		"*/System.IO.DriveInfo*",
		"*/System.IO.IsolatedStorage.*",
	};

	// Reflection-based dynamic invocation — defeats static analysis by
	// resolving call targets at runtime.
	public static readonly string[] DangerousReflection = new[]
	{
		"*/System.Reflection.MethodInfo.Invoke*",
		"*/System.Reflection.MethodBase.Invoke*",
		"*/System.Reflection.ConstructorInfo.Invoke*",
		"*/System.Reflection.PropertyInfo.GetValue*",
		"*/System.Reflection.PropertyInfo.SetValue*",
		"*/System.Reflection.FieldInfo.GetValue*",
		"*/System.Reflection.FieldInfo.SetValue*",
		"*/System.Activator.CreateInstance*",
		"!*/System.Activator.CreateInstance<T>()", // generic-only is engine-whitelisted; allow that exact form
		"*/System.Delegate.CreateDelegate*",
		"*/System.Type.GetType*",
		"*/System.Type.InvokeMember*",
		"*/System.Type.GetMethods*",
		"*/System.Type.GetMethod*",
		"*/System.Type.GetFields*",
		"*/System.Type.GetField*",
		"*/System.Type.GetProperties*",
		"*/System.Type.GetProperty*",
		"*/System.Type.GetConstructors*",
		"*/System.Type.GetConstructor*",
		"*/System.Type.GetMembers*",
		"*/System.Type.GetMember*",
	};

	// Environment + registry + OS info gathering.
	public static readonly string[] Environment = new[]
	{
		"*/System.Environment.GetEnvironmentVariable*",
		"*/System.Environment.GetEnvironmentVariables*",
		"*/System.Environment.GetCommandLineArgs*",
		"*/System.Environment.get_UserName*",
		"*/System.Environment.get_UserDomainName*",
		"*/System.Environment.get_MachineName*",
		"*/System.Environment.GetFolderPath*",
		"*/System.Environment.SetEnvironmentVariable*",
		"*/System.Environment.Exit*",
		"*/System.Environment.FailFast*",
		"*/Microsoft.Win32.Registry*",
		"*/Microsoft.Win32.RegistryKey*",
	};

	// String literal needles for the scanner to flag in `ldstr` operands and
	// in source string-literal nodes. Presence of these often signals native
	// shellouts, DLL names being loaded dynamically, or Win32 attack tooling.
	public static readonly string[] SuspiciousStringLiterals = new[]
	{
		"kernel32", "ntdll", "advapi32", "user32", "gdi32",
		"shell32", "ws2_32", "wininet", "urlmon", "iphlpapi",
		"powershell", "cmd.exe", "/bin/sh", "/bin/bash",
		"rundll32", "regsvr32", "mshta.exe", "wmic",
		"VirtualAlloc", "VirtualProtect",
		"CreateRemoteThread", "WriteProcessMemory", "ReadProcessMemory",
		"NtCreateSection", "RtlMoveMemory", "SetWindowsHookEx",
	};

	public static IEnumerable<string> AllRulePatterns()
	{
		foreach ( var r in Interop ) yield return r;
		foreach ( var r in Process ) yield return r;
		foreach ( var r in DynamicCode ) yield return r;
		foreach ( var r in RawNetwork ) yield return r;
		foreach ( var r in Filesystem ) yield return r;
		foreach ( var r in DangerousReflection ) yield return r;
		foreach ( var r in Environment ) yield return r;
	}

	// Categorise a rule pattern back to the family it belongs to, for
	// human-readable finding messages.
	public static string CategoryOf( string pattern )
	{
		foreach ( var r in Interop ) if ( r == pattern ) return "Interop";
		foreach ( var r in Process ) if ( r == pattern ) return "Process";
		foreach ( var r in DynamicCode ) if ( r == pattern ) return "DynamicCode";
		foreach ( var r in RawNetwork ) if ( r == pattern ) return "RawNetwork";
		foreach ( var r in Filesystem ) if ( r == pattern ) return "Filesystem";
		foreach ( var r in DangerousReflection ) if ( r == pattern ) return "DangerousReflection";
		foreach ( var r in Environment ) if ( r == pattern ) return "Environment";
		return "Unknown";
	}

	public static RuleSet BuildMatcher()
	{
		var rs = new RuleSet();
		foreach ( var r in AllRulePatterns() )
			rs.AddRule( r );
		return rs;
	}
}
