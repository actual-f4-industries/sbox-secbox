using System;
using System.Linq;
using Editor;
using Sandbox.SecBox.Bridge;
using Sandbox.SecBox.Bridge.Dto;
using Sandbox.SecBox.Lifecycle;
using DiagnosticsLog = Sandbox.SecBox.Bridge.DiagnosticsLog;

namespace Sandbox.SecBox.UI;

// Compact status card injected into the Library Manager's detail pane. Shows
// the trust-store verdict for whatever package the detail pane is currently
// displaying, plus a Re-scan button. Read-only with respect to engine state;
// only writes to the SecBox trust store via the existing ReviewDialog flow.
//
// The host (LibraryDetailPanel) feeds us the live "current library" via the
// resolver delegate so this widget doesn't need its own reflection logic.
public sealed class SecboxStatusPanel : Widget
{
	readonly Func<object> _currentLibraryResolver;

	Label _identLabel;
	Label _statusLabel;
	Label _countsLabel;
	Button _rescanButton;
	Button _reviewButton;

	string _lastIdentShown;
	int _lastStoreVersion = -1;

	const string CssCard    = "background-color: #1f2024; border-radius: 6px; padding: 8px 10px;";
	const string CssHeader  = "color: #c5cad1; font-size: 11px; font-weight: 700; letter-spacing: 0.5px;";
	const string CssIdent   = "color: #e8eaee; font-size: 12px; font-family: 'Consolas','Menlo',monospace;";
	const string CssStatus  = "color: #9aa0a6; font-size: 11px;";
	const string CssCounts  = "color: #c5cad1; font-size: 11px; font-family: monospace;";

	public SecboxStatusPanel( Widget parent, Func<object> currentLibraryResolver ) : base( parent )
	{
		_currentLibraryResolver = currentLibraryResolver ?? (() => null);

		Layout = Layout.Column();
		Layout.Margin = 8;
		Layout.Spacing = 4;
		SetStyles( CssCard );

		var header = new Label( "SECBOX" );
		header.SetStyles( CssHeader );
		Layout.Add( header );

		_identLabel = new Label( "(no library selected)" );
		_identLabel.SetStyles( CssIdent );
		_identLabel.WordWrap = true;
		Layout.Add( _identLabel );

		_statusLabel = new Label( "" );
		_statusLabel.SetStyles( CssStatus );
		Layout.Add( _statusLabel );

		_countsLabel = new Label( "" );
		_countsLabel.SetStyles( CssCounts );
		Layout.Add( _countsLabel );

		var buttonRow = Layout.AddRow();
		buttonRow.Spacing = 6;

		_rescanButton = new Button( "Re-scan" );
		_rescanButton.Icon = "refresh";
		_rescanButton.Clicked = OnRescan;
		buttonRow.Add( _rescanButton );

		_reviewButton = new Button( "Open Review…" );
		_reviewButton.Icon = "policy";
		_reviewButton.Clicked = OnOpenReview;
		buttonRow.Add( _reviewButton );

		buttonRow.AddStretchCell();
	}

	// Called by host on a low-frequency tick. Cheap when the displayed library
	// hasn't changed (early-out on _lastIdentShown comparison).
	public void Refresh()
	{
		try { RefreshImpl(); }
		catch ( Exception ex ) { DiagnosticsLog.Warn( $"SecboxStatusPanel.Refresh: {ex.Message}" ); }
	}

	void RefreshImpl()
	{
		var lib = _currentLibraryResolver();
		var (ident, folder) = ResolveIdentAndFolder( lib );

		// Early-out: same library AND trust store unchanged. We still recompute
		// when TrustStore.Save() bumps its version (e.g. user clicked Block in
		// the review dialog) so the status text updates without a click-away.
		var storeVersion = TrustStore.Version;
		if ( ident == _lastIdentShown && storeVersion == _lastStoreVersion ) return;
		_lastIdentShown = ident;
		_lastStoreVersion = storeVersion;

		if ( string.IsNullOrEmpty( ident ) )
		{
			_identLabel.Text = "(no library selected)";
			_statusLabel.Text = "";
			_countsLabel.Text = "";
			_rescanButton.Enabled = false;
			_reviewButton.Enabled = false;
			return;
		}

		_identLabel.Text = ident;
		_rescanButton.Enabled = !string.IsNullOrEmpty( folder );
		_reviewButton.Enabled = !string.IsNullOrEmpty( folder );

		var projectRoot = PackageLocator.CurrentProjectRoot();
		if ( string.IsNullOrEmpty( projectRoot ) )
		{
			_statusLabel.Text = "no open project";
			_countsLabel.Text = "";
			return;
		}

		if ( string.IsNullOrEmpty( folder ) )
		{
			_statusLabel.Text = "engine/external package — out of scope";
			_countsLabel.Text = "";
			return;
		}

		string hash;
		try { hash = PackageHasher.HashFolder( folder ); }
		catch ( Exception ex )
		{
			_statusLabel.Text = $"hash failed: {ex.Message}";
			_countsLabel.Text = "";
			return;
		}

		var store = TrustStore.Load( projectRoot );
		var entry = store.Find( hash );
		if ( entry == null )
		{
			_statusLabel.Text = "not yet scanned — click Re-scan";
			_countsLabel.Text = "";
			return;
		}

		_statusLabel.Text = $"{entry.Decision} · reviewed {entry.ReviewedAt:yyyy-MM-dd HH:mm}";
		_countsLabel.Text = $"Crit={entry.CriticalCount}  High={entry.HighCount}  Med={entry.MediumCount}  Low={entry.LowCount}";
	}

	void OnRescan()
	{
		var lib = _currentLibraryResolver();
		var (ident, folder) = ResolveIdentAndFolder( lib );
		if ( string.IsNullOrEmpty( ident ) || string.IsNullOrEmpty( folder ) ) return;

		_statusLabel.Text = "scanning…";
		_lastIdentShown = null; // force refresh after scan

		// Off the UI thread — the Core scan can take seconds for big libs and
		// holds GetAwaiter().GetResult internally that would deadlock here.
		System.Threading.Tasks.Task.Run( () => RunScan( ident, folder ) );
	}

	void RunScan( string ident, string folder )
	{
		try
		{
			SecboxCoreClient.EnsureReadyAsync().GetAwaiter().GetResult();
			var report = SecboxCoreClient.ScanFolder( folder );

			var critical = report.Findings.Count( f => f.Severity == Severity.Critical );
			var high     = report.Findings.Count( f => f.Severity == Severity.High );
			var medium   = report.Findings.Count( f => f.Severity == Severity.Medium );
			var low      = report.Findings.Count( f => f.Severity == Severity.Low );

			var projectRoot = PackageLocator.CurrentProjectRoot();
			if ( string.IsNullOrEmpty( projectRoot ) ) return;

			var hash = PackageHasher.HashFolder( folder );
			var store = TrustStore.Load( projectRoot );

			store.Upsert( new TrustEntry
			{
				PackageIdent = ident,
				ContentHash = hash,
				Decision = store.Find( hash )?.Decision ?? Decision.NotReviewed,
				ReviewedAt = DateTime.UtcNow,
				CriticalCount = critical,
				HighCount = high,
				MediumCount = medium,
				LowCount = low,
				Notes = $"Manual re-scan from Library Manager. {report.Findings.Count} findings.",
			} );
			store.Save();

			DiagnosticsLog.Info( $"Library Manager re-scan {ident}: Crit={critical} High={high} Med={medium} Low={low}" );

			var maxSev = report.Findings.Count == 0 ? Severity.Info : report.Findings.Max( f => f.Severity );
			if ( maxSev >= store.Policy.PromptThreshold )
				ReviewDialog.Show( ident, hash, report.Findings, store );

			MainThread.Queue( Refresh );
		}
		catch ( Exception ex )
		{
			DiagnosticsLog.Error( $"Library Manager re-scan {ident} failed", ex );
			MainThread.Queue( () =>
			{
				_statusLabel.Text = $"scan failed: {ex.Message}";
			} );
		}
	}

	void OnOpenReview()
	{
		var lib = _currentLibraryResolver();
		var (ident, folder) = ResolveIdentAndFolder( lib );
		if ( string.IsNullOrEmpty( ident ) || string.IsNullOrEmpty( folder ) ) return;

		// Re-scan synchronously enough to populate the review dialog. If a
		// recent trust entry exists this is fine; otherwise the user effectively
		// gets a fresh scan + review in one click.
		System.Threading.Tasks.Task.Run( () =>
		{
			try
			{
				SecboxCoreClient.EnsureReadyAsync().GetAwaiter().GetResult();
				var report = SecboxCoreClient.ScanFolder( folder );
				var projectRoot = PackageLocator.CurrentProjectRoot();
				if ( string.IsNullOrEmpty( projectRoot ) ) return;
				var hash = PackageHasher.HashFolder( folder );
				var store = TrustStore.Load( projectRoot );
				ReviewDialog.Show( ident, hash, report.Findings, store );
			}
			catch ( Exception ex )
			{
				DiagnosticsLog.Error( $"open-review of {ident} failed", ex );
			}
		} );
	}

	// Extract (ident, folder) from whatever object the detail pane is showing.
	// Accepts either a LibraryProject (installed view) or a Package (browse).
	// Returns nulls when nothing identifiable is present.
	static (string ident, string folder) ResolveIdentAndFolder( object lib )
	{
		if ( lib == null ) return (null, null);

		// LibraryProject: has .Project.Package and .Project.RootDirectory.
		if ( lib is LibraryProject lp )
		{
			try
			{
				var proj = lp.Project;
				var ident = proj?.Package?.FullIdent ?? proj?.Package?.Ident;
				var folder = proj?.RootDirectory?.FullName;
				return (ident, folder);
			}
			catch { }
		}

		// Package: has .FullIdent / .Ident, no folder (not yet installed).
		if ( lib is Package pkg )
		{
			try
			{
				var ident = pkg.FullIdent ?? pkg.Ident;
				// Try PackageLocator in case the package is already installed locally.
				var folder = PackageLocator.FolderFor( pkg );
				return (ident, folder);
			}
			catch { }
		}

		// Fallback: reflect for common shapes.
		var p = ReflectionHelpers.GetProp( lib, "Package" );
		if ( p is Package pkg2 )
		{
			try
			{
				var ident = pkg2.FullIdent ?? pkg2.Ident;
				var folder = PackageLocator.FolderFor( pkg2 );
				return (ident, folder);
			}
			catch { }
		}

		return (null, null);
	}
}
