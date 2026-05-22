using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox.SecBox.Bridge.Dto;

namespace Sandbox.SecBox.UI;

// Custom Qt review window. Two tabs:
//  - Default: plain-language summary of concerns, Critical first. Uses
//    FindingTranslator to convert RuleIds to short titles and explanations.
//  - Advanced: the original per-finding card list (technical view).
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
	TabWidget _tabs;
	ScrollArea _defaultScroll;
	ScrollArea _advancedScroll;

	const string CssCard         = "background-color: #2b2b2f; border-radius: 6px; padding: 8px 10px;";
	const string CssRuleId       = "font-family: 'Consolas','Menlo',monospace; color: #c5cad1; font-size: 11px;";
	const string CssMessage      = "color: #e8eaee; font-size: 13px;";
	const string CssLocation     = "color: #9aa0a6; font-size: 11px; font-family: monospace;";
	const string CssFixHint      = "color: #81c784; font-size: 11px;";
	const string CssChipBase     = "color: white; padding: 3px 9px; border-radius: 10px; font-size: 11px; font-weight: 700;";
	const string CssH1           = "font-size: 18px; font-weight: 700; color: #ffffff;";
	const string CssSubtle       = "color: #9aa0a6; font-size: 11px;";
	const string CssDefaultTitle = "color: #ffffff; font-size: 14px; font-weight: 700;";
	const string CssDefaultBody  = "color: #e8eaee; font-size: 12px;";
	const string CssCountTag     = "color: #9aa0a6; font-size: 11px;";

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
		BuildTabs(); // populates _tabs, _defaultScroll, _advancedScroll
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
	// rebuilds the header/chips/lists, and refreshes the title.
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
		RebuildFindingsLists();

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
			? $"secbox - CRITICAL findings in {_packageIdent}"
			: $"secbox - review {_packageIdent}";
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

	void BuildTabs()
	{
		_tabs = new TabWidget(this);

		var defaultPage = new Widget(_tabs);
		defaultPage.Layout = Layout.Column();
		defaultPage.Layout.Margin = 0;
		defaultPage.Layout.Spacing = 0;
		_defaultScroll = defaultPage.Layout.Add(new ScrollArea(defaultPage), 1);
		_defaultScroll.Canvas = new Widget(_defaultScroll);
		_defaultScroll.Canvas.Layout = Layout.Column();
		_defaultScroll.Canvas.Layout.Margin = 4;
		_defaultScroll.Canvas.Layout.Spacing = 8;

		var advancedPage = new Widget(_tabs);
		advancedPage.Layout = Layout.Column();
		advancedPage.Layout.Margin = 0;
		advancedPage.Layout.Spacing = 0;
		_advancedScroll = advancedPage.Layout.Add(new ScrollArea(advancedPage), 1);
		_advancedScroll.Canvas = new Widget(_advancedScroll);
		_advancedScroll.Canvas.Layout = Layout.Column();
		_advancedScroll.Canvas.Layout.Margin = 4;
		_advancedScroll.Canvas.Layout.Spacing = 6;

		_tabs.AddPage("Default", "shield", defaultPage);
		_tabs.AddPage("Advanced", "code", advancedPage);
		_tabs.StateCookie = "secbox.review-window.tab";

		Layout.Add(_tabs, 1);

		PopulateDefaultList();
		PopulateAdvancedList();
	}

	void RebuildFindingsLists()
	{
		if (_defaultScroll?.Canvas?.Layout != null)
		{
			_defaultScroll.Canvas.Layout.Clear(true);
			PopulateDefaultList();
		}
		if (_advancedScroll?.Canvas?.Layout != null)
		{
			_advancedScroll.Canvas.Layout.Clear(true);
			PopulateAdvancedList();
		}
	}

	// --------------------------------------------------------------
	// Default tab: grouped plain-language concerns. Critical first.
	// --------------------------------------------------------------

	void PopulateDefaultList()
	{
		var layout = _defaultScroll.Canvas.Layout;

		if (_findings.Count == 0)
		{
			var ok = new Label("No findings. Nothing to review.");
			ok.SetStyles(CssDefaultBody);
			ok.WordWrap = true;
			layout.Add(ok);
			layout.AddStretchCell();
			return;
		}

		// Group by translated title. Group severity = worst observed.
		var groups = new Dictionary<string, ConcernGroup>(StringComparer.Ordinal);
		foreach (var f in _findings)
		{
			var exp = FindingTranslator.Translate(f.RuleId);
			if (!groups.TryGetValue(exp.Title, out var g))
			{
				g = new ConcernGroup { Explanation = exp };
				groups[exp.Title] = g;
			}
			g.Findings.Add(f);
			if (f.Severity > g.WorstSeverity) g.WorstSeverity = f.Severity;
		}

		var critCount = _findings.Count(f => f.Severity == Severity.Critical);
		var highCount = _findings.Count(f => f.Severity == Severity.High);
		var summary = new Label(BuildSummaryText(critCount, highCount, _findings.Count));
		summary.SetStyles(CssDefaultBody);
		summary.WordWrap = true;
		layout.Add(summary);
		layout.AddSpacingCell(4);

		var ordered = groups.Values
			.OrderByDescending(g => g.WorstSeverity)
			.ThenBy(g => g.Explanation.Title, StringComparer.Ordinal);

		foreach (var g in ordered)
			layout.Add(BuildConcernCard(g));

		layout.AddStretchCell();
	}

	static string BuildSummaryText(int crit, int high, int total)
	{
		if (crit > 0 && high > 0)
			return $"This package has {crit} critical and {high} high-severity concern{(crit + high == 1 ? "" : "s")}. Review the items below before granting trust.";
		if (crit > 0)
			return $"This package has {crit} critical concern{(crit == 1 ? "" : "s")}. Review carefully before granting trust.";
		if (high > 0)
		{
			var rest = total - high;
			return $"This package has {high} high-severity concern{(high == 1 ? "" : "s")} and {rest} lower-severity finding{(rest == 1 ? "" : "s")}.";
		}
		return $"This package has {total} lower-severity finding{(total == 1 ? "" : "s")}. None are critical.";
	}

	Widget BuildConcernCard(ConcernGroup g)
	{
		var sev = SeverityHex(g.WorstSeverity);

		var card = new Widget { Layout = Layout.Column() };
		card.Layout.Spacing = 4;
		card.Layout.Margin = 0;
		card.SetStyles(
			"background-color: #2b2b2f; "
			+ "border-radius: 4px; "
			+ "padding: 10px 12px 10px 14px;");

		var headerRow = card.Layout.AddRow();
		headerRow.Spacing = 8;

		// SEVERITY tag - text label so colour is not the sole signal.
		var sevTag = new Label(g.WorstSeverity.ToString().ToUpperInvariant());
		sevTag.SetStyles($"{CssChipBase} background-color: {sev};");
		headerRow.Add(sevTag);

		var title = new Label(g.Explanation.Title);
		title.SetStyles(CssDefaultTitle);
		title.TextSelectable = true;
		headerRow.Add(title);
		headerRow.AddStretchCell();

		var countTag = new Label(g.Findings.Count == 1
			? "1 finding"
			: $"{g.Findings.Count} findings");
		countTag.SetStyles(CssCountTag + $"border-left: 4px solid {sev};");
		headerRow.Add(countTag);

		var plain = new Label(g.Explanation.Plain);
		plain.SetStyles(CssDefaultBody);
		plain.WordWrap = true;
		plain.TextSelectable = true;
		card.Layout.Add(plain);

		// Up to 3 example locations - keep the card compact. Full list lives on the Advanced tab.
		var allDistinct = g.Findings
			.Where(f => !string.IsNullOrEmpty(f.Location))
			.Select(f => f.Location)
			.Distinct(StringComparer.Ordinal)
			.ToList();

		if (allDistinct.Count > 0)
		{
			card.Layout.AddSpacingCell(2);
			foreach (var loc in allDistinct.Take(3))
			{
				var locLabel = new Label("· " + loc);
				locLabel.SetStyles(CssLocation);
				locLabel.WordWrap = true;
				locLabel.TextSelectable = true;
				card.Layout.Add(locLabel);
			}
			if (allDistinct.Count > 3)
			{
				var more = new Label($"… and {allDistinct.Count - 3} more - see Advanced tab");
				more.SetStyles(CssSubtle);
				card.Layout.Add(more);
			}
		}

		return card;
	}

	sealed class ConcernGroup
	{
		public FindingTranslator.Explanation Explanation;
		public Severity WorstSeverity = Severity.Low;
		public List<Finding> Findings = new();
	}

	// --------------------------------------------------------------
	// Advanced tab: original per-finding card list.
	// --------------------------------------------------------------

	void PopulateAdvancedList()
	{
		var layout = _advancedScroll.Canvas.Layout;
		var sorted = _findings.OrderByDescending(f => f.Severity).ThenBy(f => f.RuleId);
		foreach (var f in sorted)
			layout.Add(BuildFindingCard(f));
		layout.AddStretchCell();
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
			+ "padding: 8px 10px 8px 12px;");

		// Compact header line: SEVERITY + ruleId + [finderId], severity tinted.
		var ruleId = new Label($"{f.Severity.ToString().ToUpperInvariant()}  {f.RuleId}  [{f.FinderId ?? "?"}]");
		ruleId.SetStyles(
			$"{CssRuleId} color: {sev};"
			+ $"border-left: 4px solid {sev};");
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
			hint.TextSelectable = true;
			card.Layout.Add(hint);
		}

		return card;
	}

	// --------------------------------------------------------------
	// Footer (decision buttons) - unchanged.
	// --------------------------------------------------------------

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
		cancel.Clicked = () => { _onDecision(Decision.NotReviewed); Close(); };
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
