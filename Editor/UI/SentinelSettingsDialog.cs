using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox.SecBox.Bridge;
using Sandbox.SecBox.Bridge.Dto;
using Sandbox.SecBox.Lifecycle;
using DiagnosticsLog = Sandbox.SecBox.Bridge.DiagnosticsLog;

namespace Sandbox.SecBox.UI;

// Configuration dialog for runtime monitoring. Surfaces:
//   * Tier B (profiler) on/off       — config flag, requires restart of monitoring
//   * Tier A (Sentinel) on/off       — config flag + service install/uninstall buttons
//   * Capture managed stack on kernel events
//   * Path allowlist editor
//   * Live preview of recent findings (scrollable, auto-refreshing)
//
// Construction must be on the editor main thread (uses BaseWindow / Qt
// widgets). Menu handler wraps via MainThread.Queue.
public sealed class SentinelSettingsDialog : BaseWindow
{
	const string CssH1     = "font-size: 18px; font-weight: 700; color: #ffffff;";
	const string CssH2     = "font-size: 14px; font-weight: 600; color: #ffffff;";
	const string CssSubtle = "color: #9aa0a6; font-size: 11px;";
	const string CssMono   = "font-family: 'Consolas','Menlo',monospace; color: #c5cad1; font-size: 11px;";

	SecboxConfig _cfg;
	Label _statusLine;
	ListView _findingsList;
	List<RuntimeFinding> _findingSnapshot = new();

	public SentinelSettingsDialog() : base()
	{
		_cfg = SecboxConfig.Load();

		DeleteOnClose = true;
		Size = new Vector2(720, 600);
		WindowTitle = "secbox — runtime monitoring";
		SetWindowIcon("monitor_heart");

		Layout = Layout.Column();
		Layout.Margin = 16;
		Layout.Spacing = 10;

		BuildHeader();
		BuildTierBSection();
		BuildTierASection();
		BuildAdvancedSection();
		BuildLivePreview();
		BuildFooter();

		RefreshStatusLine();

		// Live updates as findings arrive.
		RuntimeMonitorCoordinator.FindingReceived += OnFinding;
		Destroyed += () => RuntimeMonitorCoordinator.FindingReceived -= OnFinding;
	}

	public event Action Destroyed;
	public override void OnDestroyed() { try { Destroyed?.Invoke(); } catch { } base.OnDestroyed(); }

	void BuildHeader()
	{
		var col = Layout.AddColumn();
		col.Spacing = 4;
		var title = new Label("Runtime monitoring");
		title.SetStyles(CssH1);
		col.Add(title);
		var sub = new Label("Watches what installed libraries actually do at runtime. "
			+ "Tier B always-on (in-process profiler). Tier A opt-in (kernel-level via signed Windows Service).");
		sub.SetStyles(CssSubtle);
		sub.WordWrap = true;
		col.Add(sub);

		_statusLine = new Label("");
		_statusLine.SetStyles(CssMono);
		col.Add(_statusLine);
	}

	void BuildTierBSection()
	{
		var box = AddSectionBox("Tier B — CLR Profiler (recommended, default-on)");
		var cb = new Checkbox("Enable in-process profiler");
		cb.Value = _cfg.RuntimeMonitoringEnabled;
		cb.Bind("Value").From(() => _cfg.RuntimeMonitoringEnabled, v => { _cfg.RuntimeMonitoringEnabled = v; });
		box.Add(cb);
		var note = new Label("Attaches a tiny native CLR profiler (shipped with Secbox.Core) "
			+ "via DiagnosticsClient. No admin required. Observes assembly loads, JIT, dynamic codegen, "
			+ "exceptions. Disable to fully stop runtime monitoring.");
		note.SetStyles(CssSubtle);
		note.WordWrap = true;
		box.Add(note);
	}

	void BuildTierASection()
	{
		var box = AddSectionBox("Tier A — Sentinel (kernel monitoring, opt-in)");
		var cb = new Checkbox("Enable Sentinel sidecar (requires installed Windows Service)");
		cb.Value = _cfg.SentinelEnabled;
		cb.Bind("Value").From(() => _cfg.SentinelEnabled, v => { _cfg.SentinelEnabled = v; });
		box.Add(cb);
		var note = new Label("Connects to the Secbox Sentinel Windows Service for kernel-level "
			+ "visibility into file/process/network/registry calls made by the editor process. "
			+ "The service runs as LocalSystem and is the only privileged component in secbox. "
			+ "Required when monitoring native trampolines that bypass the managed runtime.");
		note.SetStyles(CssSubtle);
		note.WordWrap = true;
		box.Add(note);

		var installRow = box.AddRow();
		installRow.Spacing = 6;
		var installState = new Label("Service: …");
		installState.SetStyles(CssMono);
		installRow.Add(installState);
		installRow.AddStretchCell();

		var btnInstall = new Button("Install / Repair");
		btnInstall.Icon = "shield_lock";
		btnInstall.Clicked = () =>
		{
			// Defer to the dedicated install dialog so the user sees the
			// full explanation, pinned hash, and live download status —
			// rather than a single blocking msiexec call from inside the
			// settings panel.
			Close();
			try { new SentinelInstallDialog().Show(); }
			catch (Exception ex)
			{
				EditorUtility.DisplayDialog("secbox", $"Could not open installer dialog: {ex.Message}");
			}
		};
		installRow.Add(btnInstall);

		var btnUninstall = new Button("Uninstall");
		btnUninstall.Clicked = () =>
		{
			// msiexec /qb+ raises its own UAC prompt — that's the
			// click-through confirmation. EditorUtility.DisplayDialog is
			// info-only in s&box (returns void, no yes/no result).
			var r = SentinelInstaller.RunUninstaller();
			EditorUtility.DisplayDialog("Secbox Sentinel", r.Message);
			RefreshServiceLabel(installState);
		};
		installRow.Add(btnUninstall);

		RefreshServiceLabel(installState);
	}

	static void RefreshServiceLabel(Label l)
	{
		try
		{
			var installed = SentinelInstaller.IsServiceInstalled();
			var running = installed && SentinelInstaller.IsServiceRunning();
			l.Text = installed
				? (running ? "Service: installed & running" : "Service: installed, not running")
				: "Service: NOT installed";
		}
		catch (Exception ex) { l.Text = $"Service: query failed — {ex.Message}"; }
	}

	void BuildAdvancedSection()
	{
		var box = AddSectionBox("Advanced");

		var cbBlock = new Checkbox("Block library Process.Start (Tier E enforcement)");
		cbBlock.Value = _cfg.BlockLibraryProcessStart;
		cbBlock.Bind("Value").From(() => _cfg.BlockLibraryProcessStart,
			v => { _cfg.BlockLibraryProcessStart = v; });
		box.Add(cbBlock);
		var blockNote = new Label("When ON, the Harmony prefix in ManagedCallSensor refuses any "
			+ "Process.Start call attributed to library code (assembly name starting with "
			+ "'package.', or loaded from a Libraries/ or .bin/ path). The library sees a null "
			+ "Process / false return and typically throws on the next member access. Critical "
			+ "findings still fire the WPF alert dialog regardless of this toggle.");
		blockNote.SetStyles(CssSubtle);
		blockNote.WordWrap = true;
		box.Add(blockNote);

		var cbDialog = new Checkbox("Show WPF alert dialog on Critical findings");
		cbDialog.Value = _cfg.ShowDetectionDialog;
		cbDialog.Bind("Value").From(() => _cfg.ShowDetectionDialog,
			v => { _cfg.ShowDetectionDialog = v; });
		box.Add(cbDialog);

		var cb = new Checkbox("Capture managed stack on kernel events (expensive)");
		cb.Value = _cfg.CaptureStackOnKernelEvents;
		cb.Bind("Value").From(() => _cfg.CaptureStackOnKernelEvents,
			v => { _cfg.CaptureStackOnKernelEvents = v; });
		box.Add(cb);

		// Path allowlist is edited directly in the config JSON. Avoids
		// guessing at s&box's TextEdit API (which differs across engine
		// versions) and gives users a hand-editable record they can audit
		// or check into source control alongside the project.
		var current = _cfg.SentinelPathAllowlist?.Count > 0
			? $"{_cfg.SentinelPathAllowlist.Count} pattern(s) configured"
			: "(none — all paths under project root forward)";

		var allowLabel = new Label($"Sentinel path allowlist: {current}");
		allowLabel.SetStyles(CssSubtle);
		allowLabel.WordWrap = true;
		box.Add(allowLabel);

		var hint = new Label("Edit `sentinelPathAllowlist` in the config JSON to set glob patterns "
			+ "(one per array entry). Empty array forwards all paths under the project root.");
		hint.SetStyles(CssSubtle);
		hint.WordWrap = true;
		box.Add(hint);

		var openBtn = new Button("Open config.json");
		openBtn.Icon = "edit";
		openBtn.Clicked = () =>
		{
			try
			{
				if (!System.IO.File.Exists(SecboxConfig.FilePath))
					new SecboxConfig().Save();
				System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
				{
					FileName = SecboxConfig.FilePath,
					UseShellExecute = true,
				});
			}
			catch (Exception ex)
			{
				EditorUtility.DisplayDialog("secbox", $"Could not open config: {ex.Message}");
			}
		};
		box.Add(openBtn);
	}

	void BuildLivePreview()
	{
		var box = AddSectionBox("Recent findings (live)");
		_findingsList = new ListView();
		_findingsList.MinimumSize = new Vector2(680, 140);
		_findingsList.ItemSize = new Vector2(680, 22);
		box.Add(_findingsList);
		RefreshFindings();
	}

	void BuildFooter()
	{
		var row = Layout.AddRow();
		row.Spacing = 8;
		row.AddStretchCell();

		var cancel = new Button("Cancel");
		cancel.Clicked = () => Close();
		row.Add(cancel);

		var save = new Button.Primary("Save & apply");
		save.Icon = "save";
		save.Clicked = () =>
		{
			try
			{
				_cfg.Save();
				RuntimeMonitorCoordinator.ReapplySettings();
				DiagnosticsLog.Info("sentinel settings saved and applied");
				Close();
			}
			catch (Exception ex)
			{
				EditorUtility.DisplayDialog("secbox", $"Could not apply settings: {ex.Message}");
			}
		};
		row.Add(save);
	}

	Layout AddSectionBox(string title)
	{
		var box = Layout.AddColumn();
		box.Spacing = 6;
		var h = new Label(title);
		h.SetStyles(CssH2);
		box.Add(h);
		return box;
	}

	void RefreshStatusLine()
	{
		var attached = RuntimeMonitorCoordinator.IsAttached;
		var count = RuntimeMonitorCoordinator.RecentCount;
		_statusLine.Text = attached
			? $"Status: attached. {count} recent finding(s) buffered."
			: "Status: not attached. Settings will apply on next attach.";
	}

	void RefreshFindings()
	{
		_findingSnapshot = RuntimeMonitorCoordinator.RecentFindings.Reverse().Take(40).ToList();
		_findingsList.SetItems(_findingSnapshot.Cast<object>());
		_findingsList.ItemPaint = PaintFinding;
	}

	void PaintFinding(VirtualWidget w)
	{
		if (w.Object is not RuntimeFinding f) return;
		var sev = f.Severity switch
		{
			"Critical" => Color.Parse("#e53935").Value,
			"High"     => Color.Parse("#fb8c00").Value,
			"Medium"   => Color.Parse("#fdd835").Value,
			"Low"      => Color.Parse("#90a4ae").Value,
			_           => Color.Parse("#607d8b").Value,
		};
		Paint.Antialiasing = true;
		Paint.SetPen(sev);
		Paint.DrawText(w.Rect.Shrink(4, 2), $"[{f.Severity}] {f.Kind} → {Trunc(f.Target, 60)}  "
			+ (string.IsNullOrEmpty(f.CallerAssembly) ? "(unattributed)" : $"by {f.CallerAssembly}"));
	}

	void OnFinding(RuntimeFinding _)
	{
		try { MainThread.Queue(() => { RefreshStatusLine(); RefreshFindings(); }); } catch { }
	}

	static string Trunc(string s, int max) =>
		string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";
}
