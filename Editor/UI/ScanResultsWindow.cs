using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox.SecBox.Bridge.Dto;
using Sandbox.SecBox.Lifecycle;
using DiagnosticsLog = Sandbox.SecBox.Bridge.DiagnosticsLog;

namespace Sandbox.SecBox.UI;

// Results dialog for the manual "Scan now" menu action. Shows one card per
// library: a plain-language list of what the library can do (spawn processes,
// read and write files, call native code, ...), its finding counts, current
// trust decision, and a Review button that opens the full ReviewDialog.
//
// Opened in a "scanning" state by MenuItems.ScanNow, then populated via
// SetResults once BootAudit.ScanAllLibraries finishes off the UI thread.
public sealed class ScanResultsWindow : BaseWindow
{
	static ScanResultsWindow _instance;

	Label _subtitle;
	ScrollArea _scroll;

	const string CssH1     = "font-size: 18px; font-weight: 700; color: #ffffff;";
	const string CssSubtle = "color: #9aa0a6; font-size: 11px;";
	const string CssCard   = "background-color: #2b2b2f; border-radius: 4px; padding: 10px 12px;";
	const string CssIdent  = "color: #ffffff; font-size: 14px; font-weight: 700;";
	const string CssCounts = "color: #c5cad1; font-size: 11px; font-family: monospace;";
	const string CssBody   = "color: #e8eaee; font-size: 12px;";
	const string CssDim    = "color: #9aa0a6; font-size: 11px;";
	const string CssError  = "color: #ef9a9a; font-size: 12px;";
	const string CssChip   = "color: white; padding: 3px 9px; border-radius: 10px; font-size: 11px; font-weight: 700;";

	public static ScanResultsWindow OpenScanning()
	{
		if (_instance == null)
			_instance = new ScanResultsWindow();

		try { _instance.Raise(); } catch { }
		_instance.ShowScanning();
		_instance.Show();
		return _instance;
	}

	ScanResultsWindow() : base()
	{
		DeleteOnClose = true;
		Size = new Vector2(720, 640);
		WindowTitle = "secbox - scan results";
		SetWindowIcon("radar");

		Layout = Layout.Column();
		Layout.Margin = 16;
		Layout.Spacing = 12;

		BuildHeader();

		_scroll = Layout.Add(new ScrollArea(this), 1);
		_scroll.Canvas = new Widget(_scroll);
		_scroll.Canvas.Layout = Layout.Column();
		_scroll.Canvas.Layout.Margin = 2;
		_scroll.Canvas.Layout.Spacing = 8;

		BuildFooter();
	}

	void BuildHeader()
	{
		var col = Layout.AddColumn();
		col.Spacing = 4;

		var title = new Label("Scan results");
		title.SetStyles(CssH1);
		col.Add(title);

		_subtitle = new Label("");
		_subtitle.SetStyles(CssSubtle);
		_subtitle.WordWrap = true;
		col.Add(_subtitle);
	}

	void BuildFooter()
	{
		var row = Layout.AddRow();
		row.Spacing = 8;

		var manage = new Button("Manage trust...");
		manage.Icon = "verified_user";
		manage.Clicked = () => { try { TrustManagerWindow.Open(); } catch { } };
		row.Add(manage);

		row.AddStretchCell();

		var close = new Button.Primary("Close");
		close.Clicked = () => Close();
		row.Add(close);
	}

	void ShowScanning()
	{
		_subtitle.Text = "Scanning libraries...";
		var layout = _scroll?.Canvas?.Layout;
		if (layout == null) return;
		layout.Clear(true);

		var msg = new Label("Scanning installed libraries. This can take a few seconds...");
		msg.SetStyles(CssBody);
		msg.WordWrap = true;
		layout.Add(msg);
		layout.AddStretchCell();
	}

	// Called on the main thread once the scan completes. Null means the scan
	// could not run (no project / core load failure).
	public void SetResults(List<LibraryScanResult> results)
	{
		var layout = _scroll?.Canvas?.Layout;
		if (layout == null) return;
		layout.Clear(true);

		if (results == null)
		{
			_subtitle.Text = "Scan could not run.";
			var msg = new Label("The scan did not run. Make sure a project is open and Secbox.Core loaded, then try again. See secbox > Dev > Open Diagnostics Log.");
			msg.SetStyles(CssBody);
			msg.WordWrap = true;
			layout.Add(msg);
			layout.AddStretchCell();
			return;
		}

		var flagged = results.Count(r => r.TotalFindings > 0);
		var clean = results.Count(r => !r.HasError && r.TotalFindings == 0);
		var errored = results.Count(r => r.HasError);
		_subtitle.Text = $"{results.Count} library(ies) scanned - {flagged} with findings, {clean} clean"
			+ (errored > 0 ? $", {errored} failed" : "");

		if (results.Count == 0)
		{
			var msg = new Label("No libraries in scope. Install a library under Libraries/ and scan again.");
			msg.SetStyles(CssBody);
			msg.WordWrap = true;
			layout.Add(msg);
			layout.AddStretchCell();
			return;
		}

		// Worst-first: errors, then by severity, then most findings.
		var ordered = results
			.OrderByDescending(r => r.HasError)
			.ThenByDescending(r => (int)r.MaxSeverity)
			.ThenByDescending(r => r.TotalFindings)
			.ThenBy(r => r.PackageIdent, StringComparer.OrdinalIgnoreCase);

		foreach (var r in ordered)
			layout.Add(BuildResultCard(r));

		layout.AddStretchCell();
	}

	Widget BuildResultCard(LibraryScanResult r)
	{
		var card = new Widget();
		card.Layout = Layout.Column();
		card.Layout.Spacing = 4;
		card.Layout.Margin = 0;
		card.SetStyles(CssCard);

		// Header: ident + status chip + (decision chip when there are findings).
		var headerRow = card.Layout.AddRow();
		headerRow.Spacing = 8;

		var ident = new Label(string.IsNullOrEmpty(r.PackageIdent) ? "(unknown package)" : r.PackageIdent);
		ident.SetStyles(CssIdent);
		ident.TextSelectable = true;
		headerRow.Add(ident);
		headerRow.AddStretchCell();

		if (r.HasError)
		{
			headerRow.Add(Chip("Scan failed", "#e53935"));
		}
		else if (r.TotalFindings == 0)
		{
			headerRow.Add(Chip("Clean", "#43a047"));
		}
		else
		{
			headerRow.Add(Chip(r.MaxSeverity.ToString(), SeverityHex(r.MaxSeverity)));
			headerRow.Add(Chip(DecisionLabel(r.Decision), DecisionHex(r.Decision)));
		}

		if (r.HasError)
		{
			var err = new Label($"Could not scan this library: {r.Error}");
			err.SetStyles(CssError);
			err.WordWrap = true;
			card.Layout.Add(err);
			return card;
		}

		if (r.TotalFindings == 0)
		{
			var ok = new Label("Nothing risky detected. This library stays inside the engine sandbox.");
			ok.SetStyles(CssDim);
			ok.WordWrap = true;
			card.Layout.Add(ok);
			return card;
		}

		// Counts.
		var counts = new Label($"Crit={r.CriticalCount}  High={r.HighCount}  Med={r.MediumCount}  Low={r.LowCount}");
		counts.SetStyles(CssCounts);
		card.Layout.Add(counts);

		// Plain-language "what it can do" list, worst-severity first.
		card.Layout.AddSpacingCell(2);
		var what = new Label("What this library can do:");
		what.SetStyles(CssDim);
		card.Layout.Add(what);

		foreach (var capability in Capabilities(r.Findings))
		{
			var line = new Label("- " + capability);
			line.SetStyles(CssBody);
			line.WordWrap = true;
			card.Layout.Add(line);
		}

		// Action row.
		card.Layout.AddSpacingCell(4);
		var actionRow = card.Layout.AddRow();
		actionRow.Spacing = 6;
		actionRow.AddStretchCell();

		var review = new Button("Review...");
		review.Icon = "policy";
		review.Clicked = () => OpenReview(r);
		actionRow.Add(review);

		return card;
	}

	// Distinct plain-language capability titles, worst-severity first. Built from
	// the same FindingTranslator dictionary the review window uses.
	static IEnumerable<string> Capabilities(List<Finding> findings)
	{
		var worstByTitle = new Dictionary<string, Severity>(StringComparer.Ordinal);
		foreach (var f in findings)
		{
			var title = FindingTranslator.Translate(f.RuleId).Title;
			if (string.IsNullOrEmpty(title)) continue;
			if (!worstByTitle.TryGetValue(title, out var sev) || f.Severity > sev)
				worstByTitle[title] = f.Severity;
		}

		return worstByTitle
			.OrderByDescending(kv => (int)kv.Value)
			.ThenBy(kv => kv.Key, StringComparer.Ordinal)
			.Select(kv => kv.Key)
			.Take(8);
	}

	void OpenReview(LibraryScanResult r)
	{
		try
		{
			var root = PackageLocator.CurrentProjectRoot();
			if (string.IsNullOrEmpty(root))
			{
				EditorUtility.DisplayDialog("secbox", "No current project.");
				return;
			}
			var store = TrustStore.Load(root);
			ReviewDialog.Show(r.PackageIdent, r.ContentHash, r.Findings, store);
		}
		catch (Exception ex)
		{
			DiagnosticsLog.Error($"[secbox] scan results: open review for {r.PackageIdent} failed", ex);
			EditorUtility.DisplayDialog("secbox", $"Could not open review: {ex.Message}");
		}
	}

	Label Chip(string text, string hex)
	{
		var chip = new Label(text);
		chip.SetStyles($"{CssChip} background-color: {hex};");
		return chip;
	}

	static string SeverityHex(Severity s) => s switch
	{
		Severity.Critical => "#e53935",
		Severity.High     => "#fb8c00",
		Severity.Medium   => "#fdd835",
		Severity.Low      => "#90a4ae",
		_                 => "#607d8b",
	};

	static string DecisionLabel(Decision d) => d switch
	{
		Decision.TrustAlways => "Trusted",
		Decision.AllowOnce   => "Allowed (session)",
		Decision.NotReviewed => "Not reviewed",
		Decision.Block       => "Blocked",
		Decision.Quarantine  => "Quarantined",
		_                    => d.ToString(),
	};

	static string DecisionHex(Decision d) => d switch
	{
		Decision.TrustAlways => "#43a047",
		Decision.AllowOnce   => "#29b6f6",
		Decision.NotReviewed => "#90a4ae",
		Decision.Block       => "#e53935",
		Decision.Quarantine  => "#fb8c00",
		_                    => "#607d8b",
	};

	public override void OnDestroyed()
	{
		if (_instance == this) _instance = null;
		base.OnDestroyed();
	}
}
