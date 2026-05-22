using System;
using System.Linq;
using Editor;
using Sandbox.SecBox.Bridge.Dto;
using Sandbox.SecBox.Lifecycle;
using DiagnosticsLog = Sandbox.SecBox.Bridge.DiagnosticsLog;

namespace Sandbox.SecBox.UI;

// GUI for the per-project trust store (<projectRoot>/.secbox/trust.json). Lists
// every recorded package, lets the user change its decision (Trusted / Blocked /
// Quarantined / Allowed-once / Not-reviewed) or remove the record entirely.
//
// Mutations write straight through to TrustStore.Save() - same model the
// ReviewDialog flow uses - so there's no separate dirty/Save step. The store is
// hand-editable JSON; this is just a friendlier front-end. Opened via
// secbox > Trusted Libraries...
//
// Modelled on ReviewWindow: BaseWindow + ScrollArea canvas + the same CSS idioms.
public sealed class TrustManagerWindow : BaseWindow
{
	// Combo item order. Also used to map CurrentIndex back to a Decision.
	static readonly Decision[] DecisionOrder =
	{
		Decision.TrustAlways,
		Decision.AllowOnce,
		Decision.NotReviewed,
		Decision.Block,
		Decision.Quarantine,
	};

	static TrustManagerWindow _instance;

	string _projectRoot;
	TrustStore _store;
	Label _subtitle;
	ScrollArea _scroll;

	const string CssH1     = "font-size: 18px; font-weight: 700; color: #ffffff;";
	const string CssSubtle = "color: #9aa0a6; font-size: 11px;";
	const string CssCard   = "background-color: #2b2b2f; border-radius: 4px; padding: 10px 12px;";
	const string CssIdent  = "color: #ffffff; font-size: 14px; font-weight: 700;";
	const string CssMeta   = "color: #9aa0a6; font-size: 11px; font-family: 'Consolas','Menlo',monospace;";
	const string CssCounts = "color: #c5cad1; font-size: 11px; font-family: monospace;";
	const string CssNotes  = "color: #b0b6bd; font-size: 11px;";
	const string CssChip   = "color: white; padding: 3px 9px; border-radius: 10px; font-size: 11px; font-weight: 700;";
	const string CssLabel  = "color: #c5cad1; font-size: 11px;";

	public static void Open()
	{
		if (_instance != null)
		{
			try { _instance.Raise(); return; }
			catch { _instance = null; }
		}
		_instance = new TrustManagerWindow();
		_instance.Show();
	}

	TrustManagerWindow() : base()
	{
		DeleteOnClose = true;
		Size = new Vector2(720, 620);
		WindowTitle = "secbox - trusted libraries";
		SetWindowIcon("verified_user");

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

		ReloadAndRebuild();
	}

	void BuildHeader()
	{
		var col = Layout.AddColumn();
		col.Spacing = 4;

		var title = new Label("Trusted libraries");
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

		var reload = new Button("Reload");
		reload.Icon = "refresh";
		reload.Clicked = ReloadAndRebuild;
		row.Add(reload);

		var openJson = new Button("Open raw JSON");
		openJson.Icon = "data_object";
		openJson.Clicked = OnOpenRawJson;
		row.Add(openJson);

		row.AddStretchCell();

		var close = new Button.Primary("Close");
		close.Clicked = () => Close();
		row.Add(close);
	}

	void ReloadAndRebuild()
	{
		_projectRoot = PackageLocator.CurrentProjectRoot();
		if (string.IsNullOrEmpty(_projectRoot))
		{
			_store = null;
			_subtitle.Text = "No project open - open a project to manage its trust store.";
			RebuildList();
			return;
		}

		try { _store = TrustStore.Load(_projectRoot); }
		catch (Exception ex)
		{
			_store = null;
			_subtitle.Text = $"Failed to load trust store: {ex.Message}";
			RebuildList();
			return;
		}

		RebuildList();
	}

	void RebuildList()
	{
		var layout = _scroll?.Canvas?.Layout;
		if (layout == null) return;
		layout.Clear(true);

		if (_store == null)
		{
			AddEmptyState(layout, "Nothing to manage.");
			return;
		}

		var entries = _store.Entries ?? new System.Collections.Generic.List<TrustEntry>();

		var trusted   = entries.Count(e => e.Decision == Decision.TrustAlways);
		var blocked   = entries.Count(e => e.Decision == Decision.Block || e.Decision == Decision.Quarantine);
		var pending   = entries.Count(e => e.Decision == Decision.NotReviewed);
		_subtitle.Text = $"{entries.Count} package(s) · {trusted} trusted · {blocked} blocked/quarantined · {pending} not reviewed\n{_store.FilePath}";

		if (entries.Count == 0)
		{
			AddEmptyState(layout, "No packages recorded yet. Install a library or run secbox > Scan All Libraries Now.");
			return;
		}

		// Surface things that need attention first (not-reviewed, then blocked /
		// quarantined), trusted last. Stable alphabetical within each bucket.
		foreach (var e in entries.OrderBy(AttentionRank).ThenBy(e => e.PackageIdent, StringComparer.OrdinalIgnoreCase))
			layout.Add(BuildEntryCard(e));

		layout.AddStretchCell();
	}

	static int AttentionRank(TrustEntry e) => e.Decision switch
	{
		Decision.NotReviewed => 0,
		Decision.Quarantine  => 1,
		Decision.Block       => 2,
		Decision.AllowOnce   => 3,
		Decision.TrustAlways => 4,
		_                    => 5,
	};

	void AddEmptyState(Layout layout, string text)
	{
		var l = new Label(text);
		l.SetStyles(CssNotes);
		l.WordWrap = true;
		layout.Add(l);
		layout.AddStretchCell();
	}

	Widget BuildEntryCard(TrustEntry entry)
	{
		var card = new Widget();
		card.Layout = Layout.Column();
		card.Layout.Spacing = 4;
		card.Layout.Margin = 0;
		card.SetStyles(CssCard);

		// Header: ident + current-decision chip.
		var headerRow = card.Layout.AddRow();
		headerRow.Spacing = 8;

		var ident = new Label(string.IsNullOrEmpty(entry.PackageIdent) ? "(unknown package)" : entry.PackageIdent);
		ident.SetStyles(CssIdent);
		ident.TextSelectable = true;
		headerRow.Add(ident);
		headerRow.AddStretchCell();

		var chip = new Label("");
		headerRow.Add(chip);
		UpdateDecisionChip(chip, entry.Decision);

		// Meta line: version · hash · reviewed-at.
		var meta = new Label(BuildMeta(entry));
		meta.SetStyles(CssMeta);
		meta.WordWrap = true;
		meta.TextSelectable = true;
		card.Layout.Add(meta);

		// Finding counts.
		var counts = new Label($"Crit={entry.CriticalCount}  High={entry.HighCount}  Med={entry.MediumCount}  Low={entry.LowCount}");
		counts.SetStyles(CssCounts);
		card.Layout.Add(counts);

		if (!string.IsNullOrEmpty(entry.Notes))
		{
			var notes = new Label(entry.Notes);
			notes.SetStyles(CssNotes);
			notes.WordWrap = true;
			card.Layout.Add(notes);
		}

		// Action row: decision selector + remove.
		card.Layout.AddSpacingCell(4);
		var actionRow = card.Layout.AddRow();
		actionRow.Spacing = 6;

		var decisionLabel = new Label("Decision:");
		decisionLabel.SetStyles(CssLabel);
		actionRow.Add(decisionLabel);

		var combo = new ComboBox();
		foreach (var d in DecisionOrder)
			combo.AddItem(DecisionLabel(d), DecisionIcon(d));

		var startIndex = Array.IndexOf(DecisionOrder, entry.Decision);
		combo.CurrentIndex = startIndex < 0 ? 0 : startIndex;
		// Subscribe AFTER setting the initial index so the programmatic set
		// doesn't fire the handler and re-save unchanged state.
		combo.ItemChanged += () => OnDecisionChanged(entry, combo, chip);
		actionRow.Add(combo);

		actionRow.AddStretchCell();

		var remove = new Button("Remove");
		remove.Icon = "delete";
		remove.SetStyles("color: #ef9a9a;");
		remove.Clicked = () => OnRemove(entry);
		actionRow.Add(remove);

		return card;
	}

	static string BuildMeta(TrustEntry entry)
	{
		var version = string.IsNullOrEmpty(entry.Version) ? null : $"v{entry.Version}";
		var hash = string.IsNullOrEmpty(entry.ContentHash) ? "(no hash)" : entry.ContentHash[..Math.Min(12, entry.ContentHash.Length)] + "…";
		var reviewed = entry.ReviewedAt == default ? "never reviewed" : $"reviewed {entry.ReviewedAt:yyyy-MM-dd HH:mm}";
		return version == null ? $"{hash} · {reviewed}" : $"{version} · {hash} · {reviewed}";
	}

	void OnDecisionChanged(TrustEntry entry, ComboBox combo, Label chip)
	{
		var idx = combo.CurrentIndex;
		if (idx < 0 || idx >= DecisionOrder.Length) return;

		var newDecision = DecisionOrder[idx];
		if (newDecision == entry.Decision) return;
		if (_store == null) return;

		entry.Decision = newDecision;
		entry.ReviewedAt = DateTime.UtcNow;
		entry.Notes = $"Decision set to {newDecision} via Trust Manager on {DateTime.Now:yyyy-MM-dd HH:mm}.";

		try { _store.Save(); }
		catch (Exception ex)
		{
			DiagnosticsLog.Error($"[secbox] trust manager: save failed for {entry.PackageIdent}", ex);
			EditorUtility.DisplayDialog("secbox", $"Could not save trust store: {ex.Message}");
			return;
		}

		UpdateDecisionChip(chip, newDecision);
		DiagnosticsLog.Info($"[secbox] trust manager: {entry.PackageIdent} -> {newDecision}");
	}

	void OnRemove(TrustEntry entry)
	{
		if (_store == null) return;

		EditorUtility.DisplayDialog(
			"secbox - remove trust record",
			$"Remove the trust record for {entry.PackageIdent}?\n\n"
			+ "The library is not uninstalled. It will be treated as not-yet-reviewed and re-scanned on the next boot or scan.",
			"Cancel", "Remove",
			() =>
			{
				try
				{
					_store.Remove(entry.ContentHash);
					_store.Save();
					DiagnosticsLog.Info($"[secbox] trust manager: removed record for {entry.PackageIdent}");
				}
				catch (Exception ex)
				{
					DiagnosticsLog.Error($"[secbox] trust manager: remove failed for {entry.PackageIdent}", ex);
					EditorUtility.DisplayDialog("secbox", $"Could not remove record: {ex.Message}");
					return;
				}
				RebuildList();
			},
			icon: "🗑️");
	}

	void OnOpenRawJson()
	{
		if (_store == null || string.IsNullOrEmpty(_store.FilePath))
		{
			EditorUtility.DisplayDialog("secbox", "No trust store to open - open a project first.");
			return;
		}
		if (!System.IO.File.Exists(_store.FilePath))
		{
			EditorUtility.DisplayDialog("secbox", $"Trust store does not exist yet at:\n{_store.FilePath}\n\nInstall a library or run a scan to create it.");
			return;
		}
		try
		{
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
			{
				FileName = _store.FilePath,
				UseShellExecute = true,
			});
		}
		catch (Exception ex)
		{
			EditorUtility.DisplayDialog("secbox", $"Could not open: {ex.Message}\n\nPath: {_store.FilePath}");
		}
	}

	void UpdateDecisionChip(Label chip, Decision d)
	{
		chip.Text = DecisionLabel(d);
		chip.SetStyles($"{CssChip} background-color: {DecisionHex(d)};");
	}

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

	static string DecisionIcon(Decision d) => d switch
	{
		Decision.TrustAlways => "verified",
		Decision.AllowOnce   => "schedule",
		Decision.NotReviewed => "help",
		Decision.Block       => "block",
		Decision.Quarantine  => "warning",
		_                    => "policy",
	};

	public override void OnDestroyed()
	{
		if (_instance == this) _instance = null;
		base.OnDestroyed();
	}
}
