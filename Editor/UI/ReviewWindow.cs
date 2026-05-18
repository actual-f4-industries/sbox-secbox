using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox.SecBox.Bridge.Dto;

namespace Sandbox.SecBox.UI;

// Custom Qt review window. Far prettier than EditorUtility.DisplayDialog —
// color-coded severity chips, scrollable findings list with expandable
// details, four explicit-action buttons.
//
// MUST be constructed on the editor main thread. Callers should wrap via
// MainThread.Queue if they're on a thread-pool thread.
public sealed class ReviewWindow : BaseWindow
{
	readonly string _packageIdent;
	string _contentHash;
	List<Finding> _findings;
	readonly TrustStore _store;
	readonly Action<Decision> _onDecision;

	// Layout slots we rebuild when findings are merged in.
	Layout _headerSlot;
	Layout _chipsSlot;
	ScrollArea _findingsScroll;

	const string CssCard       = "background-color: #2b2b2f; border-radius: 6px; padding: 8px 10px;";
	const string CssRuleId     = "font-family: 'Consolas','Menlo',monospace; color: #c5cad1; font-size: 11px;";
	const string CssMessage    = "color: #e8eaee; font-size: 13px;";
	const string CssLocation   = "color: #9aa0a6; font-size: 11px; font-family: monospace;";
	const string CssFixHint    = "color: #81c784; font-size: 11px;";
	const string CssChipBase   = "color: white; padding: 3px 9px; border-radius: 10px; font-size: 11px; font-weight: 700;";
	const string CssH1         = "font-size: 18px; font-weight: 700; color: #ffffff;";
	const string CssSubtle     = "color: #9aa0a6; font-size: 11px;";

	public ReviewWindow(
		string packageIdent,
		string contentHash,
		IList<Finding> findings,
		TrustStore store,
		Action<Decision> onDecision) : base()
	{
		_packageIdent = packageIdent;
		_contentHash = contentHash;
		_findings = (findings ?? new List<Finding>()).ToList();
		_store = store;
		_onDecision = onDecision ?? (_ => { });

		DeleteOnClose = true;
		Size = new Vector2(820, 640);

		Layout = Layout.Column();
		Layout.Margin = 16;
		Layout.Spacing = 12;

		_headerSlot = Layout.AddColumn();
		_chipsSlot = Layout.AddRow();
		BuildFindingsList(); // populates _findingsScroll
		BuildFooter();

		RebuildHeaderAndChips();
	}

	// Live view of the currently-displayed merged findings. ReviewDialog
	// reads this when the user clicks a decision button.
	public IReadOnlyList<Finding> CurrentFindings => _findings;

	// Fires after the window is destroyed (Qt-level). ReviewDialog uses this
	// to forget the window from its open-window registry.
	public event Action Destroyed;

	public override void OnDestroyed()
	{
		try { Destroyed?.Invoke(); } catch { }
		base.OnDestroyed();
	}

	// Called by ReviewDialog when a follow-up scan of the same package fires.
	// Merges new findings (by RuleId + Location) into the existing set,
	// rebuilds the header/chips/list, and refreshes the title.
	public void MergeFindings(string contentHash, IList<Finding> incoming)
	{
		if (incoming == null || incoming.Count == 0) return;

		_contentHash = contentHash;

		var seen = new HashSet<string>(_findings.Select(f => $"{f.RuleId}|{f.Location}"));
		foreach (var f in incoming)
		{
			var key = $"{f.RuleId}|{f.Location}";
			if (seen.Add(key)) _findings.Add(f);
		}

		RebuildHeaderAndChips();
		RebuildFindingsList();

		try { Raise(); }
		catch { }
	}

	void RebuildHeaderAndChips()
	{
		var critical = _findings.Count(f => f.Severity == Severity.Critical);
		var high = _findings.Count(f => f.Severity == Severity.High);
		var medium = _findings.Count(f => f.Severity == Severity.Medium);
		var low = _findings.Count(f => f.Severity == Severity.Low);

		WindowTitle = critical > 0
			? $"secbox — CRITICAL findings in {_packageIdent}"
			: $"secbox — review {_packageIdent}";
		SetWindowIcon(critical > 0 ? "report" : "policy");

		_headerSlot.Clear(true);
		_headerSlot.Spacing = 4;
		var title = new Label(_packageIdent);
		title.SetStyles(CssH1);
		_headerSlot.Add(title);
		var sub = new Label($"Content hash {_contentHash[..16]}… · {_findings.Count} findings · scanned {DateTime.Now:HH:mm:ss}");
		sub.SetStyles(CssSubtle);
		_headerSlot.Add(sub);

		_chipsSlot.Clear(true);
		_chipsSlot.Spacing = 8;
		AddChip(_chipsSlot, "Critical", critical, "#e53935");
		AddChip(_chipsSlot, "High",     high,     "#fb8c00");
		AddChip(_chipsSlot, "Medium",   medium,   "#fdd835");
		AddChip(_chipsSlot, "Low",      low,      "#90a4ae");
		_chipsSlot.AddStretchCell();
	}

	static void AddChip(Layout row, string label, int count, string bgHex)
	{
		var chip = new Label($"{label} {count}");
		chip.SetStyles($"{CssChipBase} background-color: {bgHex};");
		row.Add(chip);
	}

	void BuildFindingsList()
	{
		_findingsScroll = Layout.Add(new ScrollArea(this), 1);
		_findingsScroll.Canvas = new Widget(_findingsScroll);
		_findingsScroll.Canvas.Layout = Layout.Column();
		_findingsScroll.Canvas.Layout.Margin = 4;
		_findingsScroll.Canvas.Layout.Spacing = 6;

		PopulateFindingsList();
	}

	void RebuildFindingsList()
	{
		if (_findingsScroll?.Canvas?.Layout == null) return;
		_findingsScroll.Canvas.Layout.Clear(true);
		PopulateFindingsList();
	}

	void PopulateFindingsList()
	{
		var sorted = _findings.OrderByDescending(f => f.Severity).ThenBy(f => f.RuleId);
		foreach (var f in sorted)
			_findingsScroll.Canvas.Layout.Add(BuildFindingCard(f));
		_findingsScroll.Canvas.Layout.AddStretchCell();
	}

	Widget BuildFindingCard(Finding f)
	{
		var sev = SeverityHex(f.Severity);

		var card = new Widget();
		card.Layout = Layout.Column();
		card.Layout.Spacing = 3;
		card.Layout.Margin = 0;
		// 4px colored left border via CSS. Padding-left is bumped to give the
		// content room beside the stripe.
		card.SetStyles(
			"background-color: #2b2b2f; "
			+ "border-radius: 4px; "
			+ $"border-left: 4px solid {sev}; "
			+ "padding: 8px 10px 8px 12px;");

		// Compact header line: SEVERITY + ruleId + [finderId], severity tinted.
		var ruleId = new Label($"{f.Severity.ToString().ToUpperInvariant()}  {f.RuleId}  [{f.FinderId ?? "?"}]");
		ruleId.SetStyles($"{CssRuleId} color: {sev};");
		ruleId.TextSelectable = true;
		card.Layout.Add(ruleId);

		var msg = new Label(f.Message ?? "");
		msg.SetStyles(CssMessage);
		msg.WordWrap = true;
		msg.TextSelectable = true;
		card.Layout.Add(msg);

		if (!string.IsNullOrEmpty(f.Location))
		{
			var loc = new Label(f.Location);
			loc.SetStyles(CssLocation);
			loc.WordWrap = true;
			loc.TextSelectable = true;
			card.Layout.Add(loc);
		}

		if (!string.IsNullOrEmpty(f.FixHint))
		{
			var hint = new Label("→ " + f.FixHint);
			hint.SetStyles(CssFixHint);
			hint.WordWrap = true;
			card.Layout.Add(hint);
		}

		return card;
	}

	void BuildFooter()
	{
		var row = Layout.AddRow();
		row.Spacing = 8;

		var openHash = new Button("Copy hash");
		openHash.Icon = "content_copy";
		openHash.Clicked = () => { try { EditorUtility.Clipboard.Copy(_contentHash); } catch { } };
		row.Add(openHash);

		row.AddStretchCell();

		var cancel = new Button("Decide later");
		cancel.Clicked = () => { _onDecision(Decision.Unreviewed); Close(); };
		row.Add(cancel);

		var allowOnce = new Button("Allow this session");
		allowOnce.Clicked = () => { _onDecision(Decision.AllowOnce); Close(); };
		row.Add(allowOnce);

		var block = new Button("Block");
		block.Icon = "block";
		block.SetStyles("background-color: #6a1b1b; color: white;");
		block.Clicked = () => { _onDecision(Decision.Block); Close(); };
		row.Add(block);

		var trust = new Button.Primary("Trust this version");
		trust.Icon = "verified";
		trust.Clicked = () => { _onDecision(Decision.TrustAlways); Close(); };
		row.Add(trust);
	}

	static string SeverityHex(Severity s) => s switch
	{
		Severity.Critical => "#e53935",
		Severity.High     => "#fb8c00",
		Severity.Medium   => "#fdd835",
		Severity.Low      => "#90a4ae",
		_                 => "#607d8b",
	};
}
