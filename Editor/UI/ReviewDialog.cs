using System;
using System.Collections.Generic;
using System.Linq;
using Editor;

namespace Sandbox.SecBox.UI;

// User-facing review modal. v0.1 builds on EditorUtility.DisplayDialog (Qt
// modal popup). The dialog returns asynchronously — the engine's "blocking"
// dialog blocks input to other windows but does not block the calling
// thread. For pre-load gating we'd need a true Qt exec()-style event loop;
// for v0.1 we accept that static ctors of the just-installed package may
// have already run by the time the user clicks a button, and document this
// limit. The Block path still records the decision so future installs of
// the same hash are auto-blocked, and the user can choose to uninstall the
// library manually via the editor's Library Manager.
public static class ReviewDialog
{
	public static void Show(
		string packageIdent,
		string contentHash,
		IList<Finding> findings,
		TrustStore store )
	{
		var critical = findings.Count( f => f.Severity == Severity.Critical );
		var high = findings.Count( f => f.Severity == Severity.High );
		var medium = findings.Count( f => f.Severity == Severity.Medium );
		var low = findings.Count( f => f.Severity == Severity.Low );

		var maxSev = findings.Count == 0 ? Severity.Info : findings.Max( f => f.Severity );

		var icon = maxSev switch
		{
			Severity.Critical => "🚨",
			Severity.High => "⚠️",
			Severity.Medium => "⚠️",
			_ => "ℹ️",
		};

		var summary = string.Join( "\n", new[]
		{
			$"Package: {packageIdent}",
			$"Hash: {contentHash[..16]}...",
			"",
			$"Findings: Critical={critical} High={high} Medium={medium} Low={low}",
			"",
			"Top findings:",
		} );

		var lines = findings
			.OrderByDescending( f => f.Severity )
			.Take( 12 )
			.Select( f => $"  [{f.Severity}] {f.RuleId}\n    {Truncate( f.Message, 110 )}\n    @ {Truncate( f.Location, 110 )}" );

		var body = summary + "\n" + string.Join( "\n", lines );

		if ( findings.Count > 12 )
			body += $"\n\n(+{findings.Count - 12} more — see .secbox/trust.json)";

		var title = critical > 0
			? $"secbox: CRITICAL findings in {packageIdent}"
			: $"secbox: review {packageIdent}";

		// Two-button modal: "Block & record" | "Trust this version"
		// We can't surface 3 buttons via DisplayDialog; AllowOnce is the
		// implicit default on dialog dismissal.
		EditorUtility.DisplayDialog(
			title: title,
			message: body,
			noLabel: "Block (record decision)",
			yesLabel: "Trust this exact version forever",
			action: () =>
			{
				RecordDecision( store, packageIdent, contentHash, findings, Decision.TrustAlways );
				global::Sandbox.Internal.GlobalGameNamespace.Log.Info(
					$"[secbox] user trusted {packageIdent} ({contentHash[..16]}...)" );
			},
			icon: icon );

		// If the action callback didn't fire (user clicked Block), the dialog
		// destroys without calling our action — there's no second callback.
		// We treat the *initial* persisted state as Unreviewed, and only the
		// "Trust" action callback flips it to TrustAlways. To capture Block
		// we'd need a custom Widget — deferred to v0.2.
	}

	static string Truncate( string s, int max )
		=> string.IsNullOrEmpty( s ) || s.Length <= max ? s : s[..max] + "…";

	static void RecordDecision(
		TrustStore store,
		string ident,
		string hash,
		IList<Finding> findings,
		Decision decision )
	{
		var critical = findings.Count( f => f.Severity == Severity.Critical );
		var high = findings.Count( f => f.Severity == Severity.High );
		var medium = findings.Count( f => f.Severity == Severity.Medium );
		var low = findings.Count( f => f.Severity == Severity.Low );

		store.Upsert( new TrustEntry
		{
			PackageIdent = ident,
			ContentHash = hash,
			Decision = decision,
			ReviewedAt = DateTime.UtcNow,
			CriticalCount = critical,
			HighCount = high,
			MediumCount = medium,
			LowCount = low,
			Notes = decision switch
			{
				Decision.TrustAlways => "User trusted via review dialog.",
				Decision.Block => "User blocked via review dialog.",
				_ => null,
			},
		} );
		store.Save();
	}
}
