using System;
using System.Linq;
using Editor;

namespace Sandbox.SecBox.Lifecycle;

// Subscribes to AppDomain.CurrentDomain.AssemblyLoad — public, framework-level
// event that fires on every assembly load. Late-detection layer.
//
// Important limit: by the time this event fires, the assembly's static
// constructors have already run. We catch *subsequent* damage (event handlers,
// menu callbacks, queued timers) but cannot undo the initial payload. The
// pre-load install hook is the only layer that can actually prevent execution
// of new packages — the runtime monitor is defense-in-depth for packages that
// somehow slipped past it (e.g. installed when secbox was disabled, or in a
// session where it loaded after the malicious one).
public static class RuntimeMonitor
{
	static bool subscribed;

	public static void Subscribe()
	{
		if ( subscribed ) return;
		subscribed = true;

		try
		{
			AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
			global::Sandbox.Internal.GlobalGameNamespace.Log.Info(
				"[secbox] runtime monitor armed" );
		}
		catch ( Exception ex )
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Warning(
				$"[secbox] runtime monitor could not subscribe: {ex.Message}" );
		}
	}

	static void OnAssemblyLoad( object sender, AssemblyLoadEventArgs args )
	{
		try { Handle( args.LoadedAssembly ); }
		catch ( Exception ex )
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Error(
				$"[secbox] runtime monitor threw on assembly load: {ex.Message}" );
		}
	}

	static void Handle( System.Reflection.Assembly asm )
	{
		if ( asm == null ) return;

		var name = asm.GetName().Name ?? "";

		// Package code archives compile to assemblies named "package.*".
		// Library editor DLLs are <ident>.editor or simply <ident>. Anything
		// in the engine/system tree is not in scope.
		if ( !IsThirdPartyEditorLib( name ) ) return;

		var location = asm.Location;
		if ( string.IsNullOrEmpty( location ) ) return;

		global::Sandbox.Internal.GlobalGameNamespace.Log.Info(
			$"[secbox] runtime monitor: scanning newly-loaded assembly {name}" );

		var projectRoot = PackageLocator.CurrentProjectRoot();
		var store = !string.IsNullOrEmpty( projectRoot )
			? TrustStore.Load( projectRoot )
			: new TrustStore { Policy = new Policy() };

		if ( !store.Policy.RuntimeMonitor ) return;

		var scanner = new AssemblyScanner();
		var findings = scanner.Scan( location ).ToList();
		var critical = findings.Count( f => f.Severity == Severity.Critical );
		var high = findings.Count( f => f.Severity == Severity.High );

		if ( critical > 0 )
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Error(
				$"[secbox] runtime monitor: {name} has {critical} CRITICAL findings "
				+ $"AFTER load — static ctor damage may already be done. "
				+ $"First finding: {findings.First( f => f.Severity == Severity.Critical )}" );
		}
		else if ( high > 0 )
		{
			global::Sandbox.Internal.GlobalGameNamespace.Log.Warning(
				$"[secbox] runtime monitor: {name} has {high} high-severity findings (post-load)" );
		}
	}

	static bool IsThirdPartyEditorLib( string assemblyName )
	{
		if ( string.IsNullOrEmpty( assemblyName ) ) return false;

		// secbox itself
		if ( assemblyName.StartsWith( "secbox", StringComparison.OrdinalIgnoreCase ) ) return false;

		// engine + framework
		if ( assemblyName.StartsWith( "Sandbox.", StringComparison.OrdinalIgnoreCase ) ) return false;
		if ( assemblyName.StartsWith( "System.", StringComparison.OrdinalIgnoreCase ) ) return false;
		if ( assemblyName.StartsWith( "Microsoft.", StringComparison.OrdinalIgnoreCase ) ) return false;
		if ( assemblyName == "System" || assemblyName == "mscorlib"
		     || assemblyName == "netstandard" ) return false;
		if ( assemblyName == "Mono.Cecil" || assemblyName.StartsWith( "Mono.Cecil." ) ) return false;
		if ( assemblyName == "SkiaSharp" ) return false;
		if ( assemblyName.StartsWith( "Facepunch." ) ) return false;

		// well-known third-party editor extensions
		return true;
	}
}
