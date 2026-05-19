using System;
using System.Collections.Generic;
using Editor;
using Sandbox.SecBox.Bridge.Dto;
using Sandbox.SecBox.Lifecycle;
using DiagnosticsLog = Sandbox.SecBox.Bridge.DiagnosticsLog;

namespace Sandbox.SecBox.UI;

// Installs an ItemPaint wrapper on every LibraryList descendant of the
// LibraryManagerDock (there are two — ListLocal and ListGlobal — held as
// private fields, so we discover them by descendant walk rather than by
// reflecting the dock's fields).
//
// The wrapper invokes the previously-set ItemPaint (or falls through to the
// protected PaintItem on BaseItemWidget) and then overlays a small severity-
// colored stripe per row sourced from the SecBox trust store.
//
// Both lists survive across row clicks, but the engine may re-create them
// when the dock rebuilds (it has an [EditorEvent.Frame] -> Rebuild path).
// EnsureInstalled is therefore called every frame and tracks wrappers
// per-list; stale entries are pruned when the underlying widget reports
// !IsValid.
internal static class LibraryRowBadge
{
	static Type _listType;

	// Per-list state: original paint (or null) + our wrapper closure for that
	// list. Keyed by the list widget itself. References go stale when Qt
	// destroys the widget — IsValid catches that on each tick.
	sealed class ListState
	{
		public Action<VirtualWidget> Original;
		public Action<VirtualWidget> Wrapper;
	}
	static readonly Dictionary<BaseItemWidget, ListState> _wrapped = new();

	// Trust-store cache keyed by package ident. Refreshed on a low-frequency
	// timer so we don't re-load + re-hash on every frame.
	static readonly Dictionary<string, TrustSnapshot> _cache = new( StringComparer.OrdinalIgnoreCase );
	static DateTime _cacheLoadedAt = DateTime.MinValue;
	static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds( 3 );

	// True once we've ever managed to install at least one list. Cosmetic —
	// caller logs once on the install transition.
	public static bool Installed { get; private set; }

	// Idempotent per-tick installer. Returns true if at least one list is
	// currently wrapped. Cheap when nothing has changed.
	public static bool EnsureInstalled( Widget libraryManagerDock )
	{
		if ( libraryManagerDock == null || !libraryManagerDock.IsValid )
		{
			ResetState();
			return false;
		}

		if ( _listType == null )
		{
			_listType = ReflectionHelpers.ResolveEditorType(
				"Editor.LibraryManager.LibraryList", anchor: libraryManagerDock.GetType() );
			if ( _listType == null )
			{
				DiagnosticsLog.Warn( "[secbox] LibraryRowBadge: Editor.LibraryManager.LibraryList type not found — row badges disabled" );
				return false;
			}
		}

		// Prune stale entries.
		List<BaseItemWidget> dead = null;
		foreach ( var kv in _wrapped )
		{
			if ( kv.Key == null || !kv.Key.IsValid )
			{
				dead ??= new List<BaseItemWidget>();
				dead.Add( kv.Key );
			}
		}
		if ( dead != null ) foreach ( var k in dead ) _wrapped.Remove( k );

		// Walk descendants and wrap each LibraryList we don't already cover.
		int wrappedCount = 0;
		try
		{
			foreach ( var w in libraryManagerDock.GetDescendants<Widget>() )
			{
				if ( w == null || w.GetType() != _listType ) continue;
				if ( w is not BaseItemWidget list ) continue;

				if ( _wrapped.TryGetValue( list, out var state ) )
				{
					// Re-assert if something replaced our delegate (e.g. engine
					// refresh re-set ItemPaint).
					if ( !ReferenceEquals( list.ItemPaint, state.Wrapper ) )
					{
						state.Original = list.ItemPaint;
						list.ItemPaint = state.Wrapper;
					}
				}
				else
				{
					var original = list.ItemPaint;
					Action<VirtualWidget> wrapper = null;
					wrapper = vw => OnItemPaint( list, wrapper, vw );
					list.ItemPaint = wrapper;
					_wrapped[list] = new ListState { Original = original, Wrapper = wrapper };

					if ( !Installed )
					{
						Installed = true;
						DiagnosticsLog.Info( "[secbox] LibraryRowBadge installed on a LibraryList instance" );
					}
				}
				wrappedCount++;
			}
		}
		catch ( Exception ex )
		{
			DiagnosticsLog.Warn( $"[secbox] LibraryRowBadge.EnsureInstalled: walk threw: {ex.Message}" );
		}

		if ( wrappedCount == 0 && _wrapped.Count == 0 )
		{
			// No lists realized yet (dock just opened, list waiting to lay out).
			// Not an error; we'll try again next frame.
			return false;
		}

		return true;
	}

	static void OnItemPaint( BaseItemWidget list, Action<VirtualWidget> selfRef, VirtualWidget vw )
	{
		try
		{
			// Closure trick: selfRef is the wrapper itself. We look up our state
			// by the list widget to get the original paint without capturing it
			// directly (so reassert can update it later if the engine resets).
			Action<VirtualWidget> original = null;
			if ( _wrapped.TryGetValue( list, out var state ) )
				original = state.Original;

			if ( original != null )
			{
				original( vw );
			}
			else
			{
				ReflectionHelpers.InvokeNonPublic( list, "PaintItem", vw );
			}

			DrawBadge( vw );
		}
		catch ( Exception ex )
		{
			// A throw out of a paint delegate would propagate into Qt, which
			// will at best repaint forever and at worst tear down the dock.
			// Swallow + log; user keeps a working editor minus a badge.
			DiagnosticsLog.Warn( $"[secbox] LibraryRowBadge.OnItemPaint: {ex.Message}" );
		}
	}

	static void DrawBadge( VirtualWidget vw )
	{
		if ( vw.Object == null ) return;

		var ident = IdentOf( vw.Object );
		if ( string.IsNullOrEmpty( ident ) ) return;

		var snap = LookupSnapshot( ident );
		if ( !snap.Has ) return;

		// 4px wide vertical stripe on the right edge of the row.
		var r = vw.Rect;
		float stripeWidth = 4f;
		var stripe = new Rect( r.Right - stripeWidth - 2f, r.Top + 2f, stripeWidth, r.Height - 4f );

		Paint.ClearPen();
		Paint.SetBrush( SeverityColor( snap.MaxSeverity, snap.Decision ) );
		Paint.DrawRect( stripe, 1f );
	}

	static TrustSnapshot LookupSnapshot( string ident )
	{
		if ( DateTime.UtcNow - _cacheLoadedAt > CacheTtl )
			RefreshCache();

		if ( _cache.TryGetValue( ident, out var s ) ) return s;
		return TrustSnapshot.Missing;
	}

	static void RefreshCache()
	{
		try
		{
			_cache.Clear();
			_cacheLoadedAt = DateTime.UtcNow;

			var projectRoot = PackageLocator.CurrentProjectRoot();
			if ( string.IsNullOrEmpty( projectRoot ) ) return;

			var store = TrustStore.Load( projectRoot );
			foreach ( var entry in store.Entries )
			{
				if ( string.IsNullOrEmpty( entry.PackageIdent ) ) continue;
				_cache[entry.PackageIdent] = new TrustSnapshot
				{
					Has = true,
					Decision = entry.Decision,
					MaxSeverity =
						entry.CriticalCount > 0 ? Severity.Critical :
						entry.HighCount     > 0 ? Severity.High     :
						entry.MediumCount   > 0 ? Severity.Medium   :
						entry.LowCount      > 0 ? Severity.Low      :
						Severity.Info,
				};
			}
		}
		catch ( Exception ex )
		{
			DiagnosticsLog.Warn( $"[secbox] LibraryRowBadge cache refresh failed: {ex.Message}" );
		}
	}

	static string IdentOf( object lib )
	{
		if ( lib is LibraryProject lp )
		{
			try
			{
				var proj = lp.Project;
				return proj?.Package?.FullIdent ?? proj?.Package?.Ident;
			}
			catch { }
		}
		if ( lib is Package pkg )
		{
			try { return pkg.FullIdent ?? pkg.Ident; }
			catch { }
		}
		return null;
	}

	static Color SeverityColor( Severity sev, Decision decision )
	{
		// Decision dominates: explicit user decisions colorize regardless of
		// finding counts so the user can see TrustAlways / Block at a glance.
		switch ( decision )
		{
			case Decision.TrustAlways: return new Color( 0.30f, 0.69f, 0.31f );  // green
			case Decision.Block:       return new Color( 0.55f, 0.11f, 0.11f );  // dark red
			case Decision.Quarantine:  return new Color( 0.50f, 0.50f, 0.50f );  // grey
		}
		return sev switch
		{
			Severity.Critical => new Color( 0.90f, 0.22f, 0.21f ),  // red
			Severity.High     => new Color( 0.98f, 0.55f, 0.00f ),  // orange
			Severity.Medium   => new Color( 0.99f, 0.85f, 0.21f ),  // yellow
			Severity.Low      => new Color( 0.56f, 0.64f, 0.68f ),  // grey-blue
			_                 => new Color( 0.38f, 0.49f, 0.55f ),  // info
		};
	}

	static void ResetState()
	{
		_wrapped.Clear();
		Installed = false;
	}

	readonly struct TrustSnapshot
	{
		public bool Has { get; init; }
		public Decision Decision { get; init; }
		public Severity MaxSeverity { get; init; }
		public static TrustSnapshot Missing => default;
	}
}
