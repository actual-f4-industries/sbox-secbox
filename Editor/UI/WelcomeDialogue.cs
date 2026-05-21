using System;
using System.IO;
using System.Threading.Tasks;
using Editor;
using Sandbox.SecBox.Bridge;
using Sandbox.SecBox.Lifecycle;

namespace Sandbox.SecBox.UI;

// One-time post-install welcome shown the first time secbox is loaded into
// a project. Trigger lives in Lifecycle.WelcomeDialogueTrigger; this file
// is pure UI. Modelled on SentinelInstallDialog - same CSS, same Layout
// idioms, same MainThread.Queue pattern for cross-thread label updates.
//
// Spelled "Dialogue" intentionally - chosen by the user despite sibling
// classes (SentinelInstallDialog, ReviewWindow, etc.) using "Dialog".
public sealed class WelcomeDialogue : BaseWindow
{
	const string CssH1     = "font-size: 18px; font-weight: 700; color: #ffffff;";
	const string CssH2     = "font-size: 13px; font-weight: 600; color: #ffffff;";
	const string CssBody   = "color: #e8eaee; font-size: 12px;";
	const string CssSubtle = "color: #9aa0a6; font-size: 11px;";

	const string GitHubUrl = "https://github.com/actual-f4-industries/sbox-secbox";

	Label _scanStatus;
	Button _btnScan;
	Checkbox _cbDontShow;
	bool _scanRunning;
	Pixmap _logo;

	public Action<WelcomeDialogueResult> Closed;

	public WelcomeDialogue() : base()
	{
		DeleteOnClose = true;
		Size = new Vector2(620, 620);
		WindowTitle = "secbox - welcome";
		SetWindowIcon("shield_lock");

		Layout = Layout.Column();
		Layout.Margin = 18;
		Layout.Spacing = 10;

		_logo = TryLoadLogo();

		BuildHeader();
		BuildWhatItDoes();
		BuildHowToUse();
		BuildScanRow();
		BuildDontShowRow();
		BuildFooter();
	}

	void BuildHeader()
	{
		if (_logo != null)
		{
			var banner = new LogoBannerWidget(_logo, this);
			banner.FixedHeight = 96;
			Layout.Add(banner);
		}

		var col = Layout.AddColumn();
		col.Spacing = 4;
		var title = new Label("Welcome to Secbox");
		title.SetStyles(CssH1);
		col.Add(title);
		var sub = new Label("Secbox is a defence-in-depth security layer for s&box editor projects. "
			+ "It scans every library you install for risky patterns, monitors runtime behaviour, "
			+ "and gives you a clear yes/no decision before trusting third-party code.");
		sub.SetStyles(CssSubtle);
		sub.WordWrap = true;
		col.Add(sub);
	}

	void BuildWhatItDoes()
	{
		AddSection("What it does",
			"On every editor boot, Secbox walks your project's Libraries/ folder and scans new or "
			+ "modified packages. Findings are recorded in <projectRoot>/.secbox/trust.json. "
			+ "Until you mark a package Trusted, runtime monitoring keeps an eye on what its code "
			+ "actually does - file writes, network calls, process spawns.");
	}

	void BuildHowToUse()
	{
		AddSection("How to use it",
			"• Install a library as usual via Library Manager.\n"
			+ "• Secbox runs a scan automatically and surfaces findings in the library row.\n"
			+ "• Review findings, mark Trusted or Blocked from the dock.\n"
			+ "• Optional: install the Sentinel sidecar for kernel-level visibility.");
	}

	void BuildScanRow()
	{
		var header = new Label("Run a scan now");
		header.SetStyles(CssH2);
		Layout.Add(header);

		var row = Layout.AddRow();
		row.Spacing = 8;

		_btnScan = new Button("Run first scan now");
		_btnScan.Icon = "radar";
		_btnScan.Clicked = OnRunScan;
		row.Add(_btnScan);

		_scanStatus = new Label("No scan run yet.");
		_scanStatus.SetStyles(CssBody);
		_scanStatus.WordWrap = true;
		row.Add(_scanStatus, 1);
	}

	void BuildDontShowRow()
	{
		_cbDontShow = new Checkbox("Don't show this welcome on any project again");
		_cbDontShow.Value = false;
		Layout.Add(_cbDontShow);

		var hint = new Label("You can re-open this dialogue any time via secbox > Show Welcome...");
		hint.SetStyles(CssSubtle);
		hint.WordWrap = true;
		Layout.Add(hint);
	}

	void BuildFooter()
	{
		var row = Layout.AddRow();
		row.Spacing = 8;

		row.AddStretchCell();

		var github = new Button("View source on GitHub");
		github.Icon = "open_in_new";
		github.Clicked = OnOpenGitHub;
		row.Add(github);

		var gotIt = new Button.Primary("Got it");
		gotIt.Icon = "check";
		gotIt.Clicked = () => Close();
		row.Add(gotIt);
	}

	void OnOpenGitHub()
	{
		try
		{
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
			{
				FileName = GitHubUrl,
				UseShellExecute = true,
			});
		}
		catch (Exception ex)
		{
			DiagnosticsLog.Warn($"[secbox] welcome: open github failed: {ex.Message}");
		}
	}

	void OnRunScan()
	{
		if (_scanRunning) return;
		_scanRunning = true;
		_btnScan.Enabled = false;
		_scanStatus.Text = "Scanning…";

		Task.Run(() =>
		{
			try
			{
				BootAudit.Run();
				MainThread.Queue(() =>
				{
					try
					{
						_scanStatus.Text = "Scan complete. See Library Manager for results.";
						_btnScan.Enabled = true;
					}
					catch { }
					_scanRunning = false;
				});
			}
			catch (Exception ex)
			{
				DiagnosticsLog.Error("[secbox] welcome: scan threw", ex);
				MainThread.Queue(() =>
				{
					try
					{
						_scanStatus.Text = $"Scan failed: {ex.Message}";
						_btnScan.Enabled = true;
					}
					catch { }
					_scanRunning = false;
				});
			}
		});
	}

	public override void OnDestroyed()
	{
		try
		{
			Closed?.Invoke(new WelcomeDialogueResult
			{
				DontShowAgainGlobally = _cbDontShow?.Value ?? false,
			});
		}
		catch (Exception ex)
		{
			DiagnosticsLog.Warn($"[secbox] welcome: Closed callback threw: {ex.Message}");
		}
		base.OnDestroyed();
	}

	void AddSection(string title, string body)
	{
		var h = new Label(title);
		h.SetStyles(CssH2);
		Layout.Add(h);

		var p = new Label(body);
		p.SetStyles(CssBody);
		p.WordWrap = true;
		Layout.Add(p);
	}

	static Pixmap TryLoadLogo()
	{
		var root = PackageLocator.CurrentSecboxLibraryRoot();
		if (string.IsNullOrEmpty(root))
		{
			DiagnosticsLog.Warn("[secbox] welcome: could not resolve secbox library root - banner skipped");
			return null;
		}

		var path = Path.Combine(root, "Assets", "Materials", "secbox-logo-transparent.png");
		if (!File.Exists(path))
		{
			DiagnosticsLog.Warn($"[secbox] welcome: logo file not found at '{path}'");
			return null;
		}

		try
		{
			var pixmap = Pixmap.FromFile(path);
			if (pixmap == null)
			{
				DiagnosticsLog.Warn($"[secbox] welcome: Pixmap.FromFile returned null for '{path}' (path scheme issue?)");
				return null;
			}
			return pixmap;
		}
		catch (Exception ex)
		{
			DiagnosticsLog.Warn($"[secbox] welcome: Pixmap.FromFile threw: {ex.Message}");
			return null;
		}
	}

	// Letterboxes a square logo into whatever rect the layout assigns. The
	// pixmap is owned by the parent dialog; this widget only paints it.
	sealed class LogoBannerWidget : Widget
	{
		readonly Pixmap _pixmap;

		public LogoBannerWidget(Pixmap pixmap, Widget parent) : base(parent)
		{
			_pixmap = pixmap;
		}

		protected override void OnPaint()
		{
			if (_pixmap == null) return;

			var size = MathF.Min(LocalRect.Width, LocalRect.Height);
			var x = LocalRect.Left + (LocalRect.Width - size) / 2f;
			var y = LocalRect.Top + (LocalRect.Height - size) / 2f;
			Paint.Draw(new Rect(x, y, size, size), _pixmap);
		}
	}
}

public sealed class WelcomeDialogueResult
{
	public bool DontShowAgainGlobally;
}
