using System;
using System.Diagnostics;
using System.Management;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Security.Principal;
using System.Runtime.InteropServices;

namespace AristotleQuarantineTool
{
    public partial class MainForm : Form
    {
        // Hardcoded target — not shown or editable in the UI
        private const string ProcessPath = @"C:\Program Files\Sergeant Laboratories, Inc\AristotleK12 Network Filter\AristotleK12_Filter.exe";
        private const string ProcessName = "AristotleK12_Filter.exe";
        private const string ServiceName = "AristotleK12 Network Filter";

        private readonly string QuarantineRoot;
        private readonly string StateFilePath;
        private readonly string ExtensionsFlagPath;
        private readonly string ExtensionsHkcuBackup;
        private readonly string ExtensionsHklmBackup;
        private readonly string ProgramsStatePath;
        private readonly string AppStatePath;
        private readonly string WarpInstalledFlag;
        private System.Windows.Forms.Timer? _monitorTimer;
        private NotifyIcon? _trayIcon;
        private bool _isMonitoring = false; // true if either background task is active
        private CancellationTokenSource? _extWatchCts;
        private CancellationTokenSource? _progWatchCts;
        private CancellationTokenSource? _filterWatchCts;
        private ManagementEventWatcher? _filterProcessWatcher;
        private bool _sessionLocked = false;
        private string _warpStatusCache = "WARP: Unknown";
        private DateTime _warpStatusCacheAt = DateTime.MinValue;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenInputDesktop(int dwFlags, bool fInherit, int dwDesiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseDesktop(IntPtr hDesktop);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern int RegNotifyChangeKeyValue(
            IntPtr hKey, bool bWatchSubtree, int dwNotifyFilter, IntPtr hEvent, bool fAsynchronous);

        private const int REG_NOTIFY_CHANGE_NAME = 0x00000001;
        private const int REG_NOTIFY_CHANGE_ATTRIBUTES = 0x00000002;
        private const int REG_NOTIFY_CHANGE_LAST_SET = 0x00000004;
        private const int REG_NOTIFY_CHANGE_SECURITY = 0x00000008;

        private static bool IsWorkstationLocked()
        {
            const int DESKTOP_SWITCHDESKTOP = 0x0100;
            IntPtr h = OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
            if (h == IntPtr.Zero)
                return true; // cannot open input desktop => locked or no access
            CloseDesktop(h);
            return false;
        }

        // Modern blue palette
        private static readonly Color BgDeep = Color.FromArgb(13, 27, 52);
        private static readonly Color BgCard = Color.FromArgb(22, 42, 78);
        private static readonly Color Accent = Color.FromArgb(0, 145, 255);
        private static readonly Color AccentHover = Color.FromArgb(40, 170, 255);
        private static readonly Color Danger = Color.FromArgb(220, 60, 70);
        private static readonly Color DangerHover = Color.FromArgb(240, 80, 90);
        private static readonly Color Success = Color.FromArgb(30, 180, 110);
        private static readonly Color SuccessHover = Color.FromArgb(50, 200, 130);
        private static readonly Color OutsideYellow = Color.FromArgb(210, 175, 40);
        private static readonly Color TextPrimary = Color.FromArgb(235, 242, 255);
        private static readonly Color TextMuted = Color.FromArgb(140, 165, 200);
        private static readonly Color LogBg = Color.FromArgb(10, 18, 36);

        public MainForm()
        {
            QuarantineRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SocratesK12");
            StateFilePath = Path.Combine(QuarantineRoot, "state.json");
            ExtensionsFlagPath = Path.Combine(QuarantineRoot, "extensions_unblocked.flag");
            ExtensionsHkcuBackup = Path.Combine(QuarantineRoot, "policies_google_hkcu.reg");
            ExtensionsHklmBackup = Path.Combine(QuarantineRoot, "policies_google_hklm.reg");
            ProgramsStatePath = Path.Combine(QuarantineRoot, "programs_unblock_state.json");
            AppStatePath = Path.Combine(QuarantineRoot, "app_state.json");
            WarpInstalledFlag = Path.Combine(QuarantineRoot, "warp_installed.flag");

            InitializeComponent();
            MainForm_Resize(this, EventArgs.Empty);
            EnsureQuarantineFolder();
            CheckAdminAndWarn();

            // Track lock/unlock so monitor only runs while unlocked
            SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;
            _sessionLocked = IsWorkstationLocked();

            if (IsAdministrator())
            {
                ReconcilePersistedState();
                // First status refresh (forceLog) re-applies enabled tweaks — not a silent always-on reinforce
                RefreshAllStatuses(forceLog: true);
                _statusTimer = new System.Windows.Forms.Timer { Interval = 5000 };
                _statusTimer.Tick += (s, e) => RefreshAllStatuses(forceLog: false); // UI only
                _statusTimer.Start();
            }
        }

        private void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock)
            {
                _sessionLocked = true;
                Log("[Monitor] Session locked — pausing enforcement.");
            }
            else if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                _sessionLocked = false;
                Log("[Monitor] Session unlocked — enforcement active.");
            }
        }

        private void InitializeComponent()
        {
            this.Text = "SocratesK12";
            this.Size = new Size(1280, 900);
            this.MinimumSize = new Size(1100, 780);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MaximizeBox = true;
            this.WindowState = FormWindowState.Maximized; // windowed fullscreen by default
            this.BackColor = BgDeep;
            this.Font = new Font("Segoe UI", 9.5F);
            this.DoubleBuffered = true;

            // Header
            var headerPanel = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(1480, 72),
                BackColor = BgCard,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            this.Controls.Add(headerPanel);

            var lblTitle = new Label
            {
                Text = "SocratesK12",
                Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold),
                ForeColor = TextPrimary,
                AutoSize = true,
                Location = new Point(24, 12)
            };
            headerPanel.Controls.Add(lblTitle);

            // Shared Refresh above both cards
            btnRefresh = CreateModernButton("Refresh Status", Accent, AccentHover);
            btnRefresh.Location = new Point(24, 84);
            btnRefresh.Size = new Size(200, 36);
            btnRefresh.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            btnRefresh.Click += (s, e) => RefreshAllStatuses(forceLog: true);
            this.Controls.Add(btnRefresh);

            btnDisableAll = CreateModernButton("DISABLE ALL AND CLOSE", Danger, DangerHover);
            btnDisableAll.Location = new Point(240, 84);
            btnDisableAll.Size = new Size(280, 36);
            btnDisableAll.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            btnDisableAll.Click += BtnDisableAll_Click;
            this.Controls.Add(btnDisableAll);

            // ===== Left box: Remove the on Device Network Filter =====
            var filterCard = new Panel
            {
                Location = new Point(24, 130),
                Size = new Size(455, 300),
                BackColor = BgCard,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom
            };
            this.Controls.Add(filterCard);
            _filterCard = filterCard;

            var lblFilter = new Label
            {
                Text = "Remove the on Device Network Filter",
                Font = new Font("Segoe UI Semibold", 11F),
                ForeColor = TextPrimary,
                Location = new Point(16, 10),
                Size = new Size(420, 28),
                AutoSize = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            filterCard.Controls.Add(lblFilter);

            lblFilterStatus = CreateStatusBadge();
            filterCard.Controls.Add(lblFilterStatus);
            filterCard.Resize += (s, e) => CenterStatusBadge(filterCard, lblFilterStatus);

            btnQuarantine = CreateModernButton("Enable Tweak", Success, SuccessHover);
            btnQuarantine.Location = new Point(16, 100);
            btnQuarantine.Size = new Size(423, 40);
            btnQuarantine.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            btnQuarantine.Click += BtnQuarantine_Click;
            filterCard.Controls.Add(btnQuarantine);

            btnRestore = CreateModernButton("Disable Tweak", Danger, DangerHover);
            btnRestore.Location = new Point(16, 150);
            btnRestore.Size = new Size(423, 40);
            btnRestore.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            btnRestore.Click += BtnRestore_Click;
            filterCard.Controls.Add(btnRestore);

            chkMonitor = new CheckBox
            {
                Text = "",
                Font = new Font("Segoe UI", 16F),
                ForeColor = Color.White,
                Location = new Point(16, 204),
                Size = new Size(32, 32),
                FlatStyle = FlatStyle.Standard,
                AutoSize = false,
                Cursor = Cursors.Hand
            };
            chkMonitor.CheckedChanged += ChkMonitor_CheckedChanged;
            filterCard.Controls.Add(chkMonitor);

            var lblFilterChk = new Label
            {
                Text = "When the filter service or program starts, stop and re-quarantine them right away (runs in the system tray) (can ONLY be enabled when the TWEAK is ALSO ENABLED)",
                Font = new Font("Segoe UI", 9F),
                ForeColor = Color.White,
                Location = new Point(54, 202),
                Size = new Size(200, 100),
                AutoSize = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                Cursor = Cursors.Hand
            };
            lblFilterChk.Click += (s, e) => { chkMonitor.Checked = !chkMonitor.Checked; };
            filterCard.Controls.Add(lblFilterChk);

            // ===== Right box: Unblock Extensions =====
            var extCard = new Panel
            {
                Location = new Point(501, 130),
                Size = new Size(455, 300),
                BackColor = BgCard,
                Anchor = AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom
            };
            this.Controls.Add(extCard);
            _extCard = extCard;

            var lblExt = new Label
            {
                Text = "Unblock Extensions",
                Font = new Font("Segoe UI Semibold", 11F),
                ForeColor = TextPrimary,
                Location = new Point(16, 12),
                AutoSize = true
            };
            extCard.Controls.Add(lblExt);

            lblExtStatus = CreateStatusBadge();
            extCard.Controls.Add(lblExtStatus);
            extCard.Resize += (s, e) => CenterStatusBadge(extCard, lblExtStatus);

            btnExtEnable = CreateModernButton("Enable Tweak", Success, SuccessHover);
            btnExtEnable.Location = new Point(16, 100);
            btnExtEnable.Size = new Size(423, 40);
            btnExtEnable.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            btnExtEnable.Click += BtnExtEnable_Click;
            extCard.Controls.Add(btnExtEnable);

            btnExtDisable = CreateModernButton("Disable Tweak", Danger, DangerHover);
            btnExtDisable.Location = new Point(16, 150);
            btnExtDisable.Size = new Size(423, 40);
            btnExtDisable.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            btnExtDisable.Click += BtnExtDisable_Click;
            extCard.Controls.Add(btnExtDisable);

            var lblExtNote = new Label
            {
                Text = "Chrome must restart for changes to take effect. Fully reversible.",
                Font = new Font("Segoe UI Semibold", 9F),
                ForeColor = Color.FromArgb(255, 200, 100),
                Location = new Point(16, 196),
                Size = new Size(200, 28),
                AutoSize = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            extCard.Controls.Add(lblExtNote);

            chkExtMonitor = new CheckBox
            {
                Text = "",
                Font = new Font("Segoe UI", 16F),
                ForeColor = Color.White,
                Location = new Point(16, 230),
                Size = new Size(32, 32),
                FlatStyle = FlatStyle.Standard,
                AutoSize = false,
                Cursor = Cursors.Hand
            };
            chkExtMonitor.CheckedChanged += ChkExtMonitor_CheckedChanged;
            extCard.Controls.Add(chkExtMonitor);

            var lblExtChk = new Label
            {
                Text = "When Windows rewrites extension policies, undo them right away (runs in the system tray) (can ONLY be enabled when the TWEAK is ALSO ENABLED)",
                Font = new Font("Segoe UI", 9F),
                ForeColor = Color.White,
                Location = new Point(54, 228),
                Size = new Size(200, 100),
                AutoSize = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                Cursor = Cursors.Hand
            };
            lblExtChk.Click += (s, e) => { chkExtMonitor.Checked = !chkExtMonitor.Checked; };
            extCard.Controls.Add(lblExtChk);

            // ===== Third box: Unblock Blocked Programs =====
            var progCard = new Panel
            {
                Location = new Point(978, 130),
                Size = new Size(455, 300),
                BackColor = BgCard,
                Anchor = AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom
            };
            this.Controls.Add(progCard);
            _progCard = progCard;

            var lblProg = new Label
            {
                Text = "Unblock Blocked Programs",
                Font = new Font("Segoe UI Semibold", 11F),
                ForeColor = TextPrimary,
                Location = new Point(16, 12),
                AutoSize = true
            };
            progCard.Controls.Add(lblProg);

            lblProgStatus = CreateStatusBadge();
            progCard.Controls.Add(lblProgStatus);
            progCard.Resize += (s, e) => CenterStatusBadge(progCard, lblProgStatus);

            btnProgEnable = CreateModernButton("Enable Tweak", Success, SuccessHover);
            btnProgEnable.Location = new Point(16, 100);
            btnProgEnable.Size = new Size(423, 40);
            btnProgEnable.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            btnProgEnable.Click += BtnProgEnable_Click;
            progCard.Controls.Add(btnProgEnable);

            btnProgDisable = CreateModernButton("Disable Tweak", Danger, DangerHover);
            btnProgDisable.Location = new Point(16, 150);
            btnProgDisable.Size = new Size(423, 40);
            btnProgDisable.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            btnProgDisable.Click += BtnProgDisable_Click;
            progCard.Controls.Add(btnProgDisable);

            chkProgMonitor = new CheckBox
            {
                Text = "",
                Font = new Font("Segoe UI", 16F),
                ForeColor = Color.White,
                Location = new Point(16, 204),
                Size = new Size(32, 32),
                FlatStyle = FlatStyle.Standard,
                AutoSize = false,
                Cursor = Cursors.Hand
            };
            chkProgMonitor.CheckedChanged += ChkProgMonitor_CheckedChanged;
            progCard.Controls.Add(chkProgMonitor);

            var lblProgChk = new Label
            {
                Text = "When Windows rewrites program-blocking policies, undo them right away (runs in the system tray) (can ONLY be enabled when the TWEAK is ALSO ENABLED)",
                Font = new Font("Segoe UI", 9F),
                ForeColor = Color.White,
                Location = new Point(54, 202),
                Size = new Size(200, 100),
                AutoSize = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                Cursor = Cursors.Hand
            };
            lblProgChk.Click += (s, e) => { chkProgMonitor.Checked = !chkProgMonitor.Checked; };
            progCard.Controls.Add(lblProgChk);

            // ===== Fourth box: WiFi Network Tweak (Cloudflare WARP) =====
            var warpCard = new Panel
            {
                Location = new Point(24, 450),
                Size = new Size(455, 300),
                BackColor = BgCard
            };
            this.Controls.Add(warpCard);
            _warpCard = warpCard;

            var lblWarp = new Label
            {
                Text = "WiFi Network Tweak",
                Font = new Font("Segoe UI Semibold", 11F),
                ForeColor = TextPrimary,
                Location = new Point(16, 12),
                AutoSize = true
            };
            warpCard.Controls.Add(lblWarp);

            lblWarpStatus = CreateStatusBadge();
            warpCard.Controls.Add(lblWarpStatus);
            warpCard.Resize += (s, e) => CenterStatusBadge(warpCard, lblWarpStatus);

            btnWarpInstall = CreateModernButton("Install VPN", Accent, AccentHover);
            btnWarpInstall.Location = new Point(16, 100);
            btnWarpInstall.Size = new Size(423, 36);
            btnWarpInstall.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            btnWarpInstall.Click += BtnWarpInstall_Click;
            warpCard.Controls.Add(btnWarpInstall);

            btnWarpEnable = CreateModernButton("Enable Tweak", Success, SuccessHover);
            btnWarpEnable.Location = new Point(16, 142);
            btnWarpEnable.Size = new Size(423, 36);
            btnWarpEnable.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            btnWarpEnable.Click += BtnWarpEnable_Click;
            warpCard.Controls.Add(btnWarpEnable);

            btnWarpDisable = CreateModernButton("Disable Tweak", Danger, DangerHover);
            btnWarpDisable.Location = new Point(16, 184);
            btnWarpDisable.Size = new Size(423, 36);
            btnWarpDisable.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            btnWarpDisable.Click += BtnWarpDisable_Click;
            warpCard.Controls.Add(btnWarpDisable);

            var lblWarpReq = new Label
            {
                Text = "Install VPN requires \"Unblock Blocked Programs\" to be Enabled first.",
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Color.FromArgb(255, 200, 120),
                Location = new Point(16, 226),
                Size = new Size(200, 40),
                AutoSize = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            warpCard.Controls.Add(lblWarpReq);

            var lblWarpHint = new Label
            {
                Text = "It is highly recommended to enable the repeating checkbox in the \"Unblock Blocked Programs\" tweak.",
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = TextMuted,
                Location = new Point(16, 268),
                Size = new Size(200, 48),
                AutoSize = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            warpCard.Controls.Add(lblWarpHint);

            // Log section (no title)
            txtLog = new TextBox
            {
                Location = new Point(24, 454),
                Size = new Size(932, 200),
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                ReadOnly = true,
                Font = new Font("Consolas", 8.5F),
                BackColor = LogBg,
                ForeColor = Color.FromArgb(160, 200, 240),
                BorderStyle = BorderStyle.None
            };
            this.Controls.Add(txtLog);

            this.Resize += MainForm_Resize;
            this.FormClosing += MainForm_FormClosing;
        }

        private void MainForm_Resize(object? sender, EventArgs e)
        {
            // 2x2 grid of tweak cards
            int margin = 24;
            int gap = 16;
            int top = 130;
            int logTopGap = 14;
            int logHeight = 140;
            int bottomMargin = 20;
            int availableWidth = this.ClientSize.Width - (margin * 2) - gap;
            int availableHeight = this.ClientSize.Height - top - logHeight - logTopGap - bottomMargin;
            int cardWidth = Math.Max(300, availableWidth / 2);
            int cardHeight = Math.Max(320, (availableHeight - gap) / 2);

            if (_filterCard != null)
            {
                _filterCard.Location = new Point(margin, top);
                _filterCard.Size = new Size(cardWidth, cardHeight);
            }
            if (_extCard != null)
            {
                _extCard.Location = new Point(margin + cardWidth + gap, top);
                _extCard.Size = new Size(cardWidth, cardHeight);
            }
            if (_progCard != null)
            {
                _progCard.Location = new Point(margin, top + cardHeight + gap);
                _progCard.Size = new Size(cardWidth, cardHeight);
            }
            if (_warpCard != null)
            {
                _warpCard.Location = new Point(margin + cardWidth + gap, top + cardHeight + gap);
                _warpCard.Size = new Size(cardWidth, cardHeight);
            }
            if (txtLog != null)
            {
                int logTop = top + (cardHeight + gap) * 2 + logTopGap;
                txtLog.Location = new Point(margin, logTop);
                txtLog.Size = new Size(this.ClientSize.Width - margin * 2, Math.Max(80, this.ClientSize.Height - logTop - bottomMargin));
            }

            if (_adminOverlay != null)
                _adminOverlay.BringToFront();
        }

        private Button CreateModernButton(string text, Color bg, Color hover)
        {
            var btn = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 10F),
                ForeColor = Color.White,
                BackColor = bg,
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = hover;
            btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(
                Math.Max(0, hover.R - 20),
                Math.Max(0, hover.G - 20),
                Math.Max(0, hover.B - 20));
            return btn;
        }

        /// <summary>Narrower, taller than buttons, rounded — status pill.</summary>
        private Label CreateStatusBadge()
        {
            var lbl = new Label
            {
                Text = "  Checking…  ",
                Font = new Font("Segoe UI Semibold", 11F),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(100, 100, 110),
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(56, 40),
                Size = new Size(280, 50), // less wide, taller than 40px buttons
                Anchor = AnchorStyles.Top
            };
            ApplyRoundedCorners(lbl, 14);
            lbl.SizeChanged += (s, e) => ApplyRoundedCorners(lbl, 14);
            return lbl;
        }

        private static void CenterStatusBadge(Control parent, Label badge)
        {
            if (parent == null || badge == null) return;
            // Keep badge narrower than full-width buttons and centered
            int targetWidth = Math.Min(280, Math.Max(180, parent.ClientSize.Width - 120));
            badge.Width = targetWidth;
            badge.Height = 50;
            badge.Left = Math.Max(16, (parent.ClientSize.Width - badge.Width) / 2);
            badge.Top = 40;
            ApplyRoundedCorners(badge, 14);
        }

        private static void ApplyRoundedCorners(Control control, int radius)
        {
            try
            {
                if (control.Width <= 0 || control.Height <= 0) return;
                int r = Math.Min(radius, Math.Min(control.Width, control.Height) / 2);
                using var path = new GraphicsPath();
                int w = control.Width;
                int h = control.Height;
                path.AddArc(0, 0, r * 2, r * 2, 180, 90);
                path.AddArc(w - r * 2, 0, r * 2, r * 2, 270, 90);
                path.AddArc(w - r * 2, h - r * 2, r * 2, r * 2, 0, 90);
                path.AddArc(0, h - r * 2, r * 2, r * 2, 90, 90);
                path.CloseFigure();
                control.Region?.Dispose();
                control.Region = new Region(path);
            }
            catch { }
        }

        private Button btnQuarantine = null!;
        private Button btnRestore = null!;
        private Button btnExtEnable = null!;
        private Button btnExtDisable = null!;
        private Button btnProgEnable = null!;
        private Button btnProgDisable = null!;
        private Button btnWarpInstall = null!;
        private Button btnWarpEnable = null!;
        private Button btnWarpDisable = null!;
        private Button btnRefresh = null!;
        private Button btnDisableAll = null!;
        private CheckBox chkMonitor = null!;
        private CheckBox chkExtMonitor = null!;
        private CheckBox chkProgMonitor = null!;
        private TextBox txtLog = null!;
        private Label lblFilterStatus = null!;
        private Label lblExtStatus = null!;
        private Label lblProgStatus = null!;
        private Label lblWarpStatus = null!;
        private Panel? _filterCard;
        private Panel? _extCard;
        private Panel? _progCard;
        private Panel? _warpCard;
        private Panel? _adminOverlay;
        private System.Windows.Forms.Timer? _statusTimer;

        private void EnsureQuarantineFolder()
        {
            try { Directory.CreateDirectory(QuarantineRoot); }
            catch (Exception ex) { Log("ERROR creating quarantine folder: " + ex.Message); }
        }

        private void CheckAdminAndWarn()
        {
            if (!IsAdministrator())
            {
                Log("WARNING: Not running as Administrator. UI locked until elevated.");
                Log("Right-click the .exe → Run as administrator.");
                ShowAdminRequiredOverlay();
            }
            else
            {
                Log("Ready. Running with Administrator privileges.");
            }
        }

        private void ShowAdminRequiredOverlay()
        {
            if (_adminOverlay != null)
            {
                _adminOverlay.BringToFront();
                return;
            }

            _adminOverlay = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(210, 10, 15, 30),
                Cursor = Cursors.No
            };

            // Block clicks to anything underneath
            _adminOverlay.Click += (s, e) => { /* swallow */ };
            _adminOverlay.MouseDown += (s, e) => { /* swallow */ };

            var center = new Panel
            {
                BackColor = Color.FromArgb(230, 180, 40, 40),
                Size = new Size(720, 280),
                Cursor = Cursors.No
            };

            var title = new Label
            {
                Text = "Run as Administrator\nfor this Program to function.",
                Font = new Font("Segoe UI Black", 28F, FontStyle.Bold),
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent
            };

            var sub = new Label
            {
                Text = "Right-click SocratesK12.exe → Run as administrator",
                Font = new Font("Segoe UI Semibold", 12F),
                ForeColor = Color.FromArgb(255, 220, 220),
                TextAlign = ContentAlignment.MiddleCenter,
                Height = 40,
                Dock = DockStyle.Bottom,
                BackColor = Color.Transparent
            };

            center.Controls.Add(title);
            center.Controls.Add(sub);
            _adminOverlay.Controls.Add(center);

            void LayoutCenter(object? s, EventArgs e)
            {
                if (_adminOverlay == null) return;
                center.Left = Math.Max(0, (_adminOverlay.ClientSize.Width - center.Width) / 2);
                center.Top = Math.Max(0, (_adminOverlay.ClientSize.Height - center.Height) / 2);
            }

            _adminOverlay.Resize += LayoutCenter;
            this.Controls.Add(_adminOverlay);
            _adminOverlay.BringToFront();
            LayoutCenter(null, EventArgs.Empty);

            // Disable interactive controls under the overlay
            try
            {
                btnQuarantine.Enabled = false;
                btnRestore.Enabled = false;
                btnExtEnable.Enabled = false;
                btnExtDisable.Enabled = false;
                btnProgEnable.Enabled = false;
                btnProgDisable.Enabled = false;
                if (btnWarpInstall != null) btnWarpInstall.Enabled = false;
                if (btnWarpEnable != null) btnWarpEnable.Enabled = false;
                if (btnWarpDisable != null) btnWarpDisable.Enabled = false;
                btnRefresh.Enabled = false;
                btnDisableAll.Enabled = false;
                if (chkMonitor != null) chkMonitor.Enabled = false;
                if (chkExtMonitor != null) chkExtMonitor.Enabled = false;
                if (chkProgMonitor != null) chkProgMonitor.Enabled = false;
            }
            catch { }
        }

        private void SetBadge(string text, Color color)
        {
            // Header status badge removed from UI — keep method so callers stay safe
        }

        private static bool IsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private void Log(string message)
        {
            try
            {
                if (txtLog == null || txtLog.IsDisposed) return;
                if (txtLog.InvokeRequired)
                {
                    txtLog.BeginInvoke(new Action(() => Log(message)));
                    return;
                }
                string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
                txtLog.AppendText(line + Environment.NewLine);
                txtLog.SelectionStart = txtLog.TextLength;
                txtLog.ScrollToCaret();
            }
            catch
            {
                // Never let logging crash the app
            }
        }

        // ===================== QUARANTINE =====================

        private async void BtnQuarantine_Click(object? sender, EventArgs e)
        {
            btnQuarantine.Enabled = false;
            btnRestore.Enabled = false;
            SetBadge("Working…", Color.FromArgb(180, 140, 40));
            try
            {
                await Task.Run(() => PerformQuarantine());
                SetBadge("Enabled", Success);
                RefreshAllStatuses(forceLog: false);
            }
            finally
            {
                btnQuarantine.Enabled = true;
                btnRestore.Enabled = true;
            }
        }

        private async void BtnExtEnable_Click(object? sender, EventArgs e)
        {
            btnExtEnable.Enabled = false;
            btnExtDisable.Enabled = false;
            try
            {
                await Task.Run(() => PerformUnblockExtensions());
                RefreshAllStatuses(forceLog: false);
            }
            finally
            {
                btnExtEnable.Enabled = true;
                btnExtDisable.Enabled = true;
            }
        }

        private async void BtnExtDisable_Click(object? sender, EventArgs e)
        {
            btnExtEnable.Enabled = false;
            btnExtDisable.Enabled = false;
            try
            {
                await Task.Run(() => PerformRestoreExtensions());
                if (chkExtMonitor != null && chkExtMonitor.Checked) chkExtMonitor.Checked = false;
                RefreshAllStatuses(forceLog: false);
            }
            finally
            {
                btnExtEnable.Enabled = true;
                btnExtDisable.Enabled = true;
            }
        }

        private void PerformUnblockExtensions()
        {
            Log("=== Unblock Extensions (Enable — reversible) ===");
            Directory.CreateDirectory(QuarantineRoot);

            bool hklmOk = BackupAndRemoveGooglePolicies(
                @"SOFTWARE\Policies\Google",
                Registry.LocalMachine,
                ExtensionsHklmBackup,
                "HKLM");
            bool hkcuOk = BackupAndRemoveGooglePolicies(
                @"Software\Policies\Google",
                Registry.CurrentUser,
                ExtensionsHkcuBackup,
                "HKCU");

            // Always mark ENABLED when user clicks Enable so status stays green
            // even if Group Policy rewrites the keys a minute later.
            try
            {
                File.WriteAllText(ExtensionsFlagPath, DateTime.Now.ToString("o"));
                Log("Extensions tweak marked Enabled (status will stay Enabled while this flag exists).");
            }
            catch (Exception ex)
            {
                Log("ERROR: Flag write failed: " + ex.Message);
            }

            // Verify current registry state for the log
            bool hklmGone = false, hkcuGone = false;
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Google");
                hklmGone = k == null;
            }
            catch { hklmGone = true; }
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Policies\Google");
                hkcuGone = k == null;
            }
            catch { hkcuGone = true; }

            if (hklmGone && hkcuGone)
                Log("Google policy keys are currently absent.");
            else
                Log("WARNING: Some Google policy keys are still present (removal may have failed, or they were rewritten). Enable the background registry-watch checkbox to keep clearing them.");

            if (!hklmOk && !hkcuOk)
                Log("Note: No policy tree was backed up (keys may already have been absent).");

            SaveAppState();
            Log("Chrome must restart for changes to take effect.");
            Log("=== DONE ===");
        }

        private void PerformRestoreExtensions()
        {
            Log("=== Unblock Extensions (Disable — restore) ===");
            bool any = false;

            if (File.Exists(ExtensionsHklmBackup))
            {
                if (RestoreRegistryBackup(ExtensionsHklmBackup, "HKLM"))
                {
                    any = true;
                    try { File.Delete(ExtensionsHklmBackup); } catch { }
                }
                else
                    Log("ERROR: HKLM restore failed — keeping backup file to avoid data loss.");
            }
            else Log("No HKLM Google policy backup found.");

            if (File.Exists(ExtensionsHkcuBackup))
            {
                if (RestoreRegistryBackup(ExtensionsHkcuBackup, "HKCU"))
                {
                    any = true;
                    try { File.Delete(ExtensionsHkcuBackup); } catch { }
                }
                else
                    Log("ERROR: HKCU restore failed — keeping backup file to avoid data loss.");
            }
            else Log("No HKCU Google policy backup found.");

            if (!HasExtensionBackups())
            {
                try
                {
                    if (File.Exists(ExtensionsFlagPath))
                        File.Delete(ExtensionsFlagPath);
                }
                catch { }
            }
            else
            {
                Log("Backups still present — Extensions stays enabled (will not destroy backups).");
                try
                {
                    if (!File.Exists(ExtensionsFlagPath))
                        File.WriteAllText(ExtensionsFlagPath, DateTime.Now.ToString("o"));
                }
                catch { }
            }

            if (!any)
                Log("Nothing to restore (tweak may not have been enabled, or no policies existed).");
            else
                Log("Original Google policy keys restored.");
            SaveAppState();
            Log("Chrome must restart for changes to take effect.");
            Log("=== DONE ===");
            TryRemoveTraceFolder();
        }

        /// <summary>
        /// Backup via .NET registry API (does not depend on reg.exe), then delete tree.
        /// Backup file is a simple line-based dump: PATH|NAME|KIND|VALUE
        /// </summary>
        private bool BackupAndRemoveGooglePolicies(
            string subKeyPath, RegistryKey hive, string backupFile, string label)
        {
            try
            {
                using var existing = hive.OpenSubKey(subKeyPath, writable: false);
                if (existing == null)
                {
                    Log($"{label}\\{subKeyPath} — not present (nothing to backup).");
                    return false;
                }
            }
            catch
            {
                Log($"{label}\\{subKeyPath} — not present.");
                return false;
            }

            bool backedUp = false;
            try
            {
                if (File.Exists(backupFile)) File.Delete(backupFile);
                var lines = new List<string>();
                lines.Add("SOCRATESK12_REG_BACKUP_V1");
                lines.Add(label + "|" + subKeyPath);
                DumpRegistryTree(hive, subKeyPath, lines);
                File.WriteAllLines(backupFile, lines);
                backedUp = File.Exists(backupFile) && new FileInfo(backupFile).Length > 0;
                if (backedUp)
                    Log($"Backed up {label}\\{subKeyPath} ({lines.Count} lines)");
                else
                    Log($"ERROR: backup empty for {label}");
            }
            catch (Exception ex)
            {
                Log($"Backup failed for {label}: {ex.Message}");
                backedUp = false;
            }

            // Still try to remove even if backup failed — user wants unblock;
            // but keep trying backup first. If backup failed, do NOT delete (data loss).
            if (!backedUp)
            {
                Log($"Will NOT remove {label}\\{subKeyPath} without a backup.");
                return false;
            }

            try
            {
                hive.DeleteSubKeyTree(subKeyPath, throwOnMissingSubKey: false);
                Log($"Removed {label}\\{subKeyPath} (reversible via Disable Tweak)");
                return true;
            }
            catch (Exception ex)
            {
                Log($"Failed to remove {label}\\{subKeyPath}: {ex.Message}");
                return false;
            }
        }

        private void DumpRegistryTree(RegistryKey hive, string subKeyPath, List<string> lines)
        {
            using var key = hive.OpenSubKey(subKeyPath, writable: false);
            if (key == null) return;

            foreach (string valueName in key.GetValueNames())
            {
                try
                {
                    var kind = key.GetValueKind(valueName);
                    object? val = key.GetValue(valueName);
                    string encoded = EncodeRegValue(kind, val);
                    string name = string.IsNullOrEmpty(valueName) ? "(default)" : valueName;
                    lines.Add($"V|{subKeyPath}|{name}|{(int)kind}|{encoded}");
                }
                catch { }
            }

            foreach (string child in key.GetSubKeyNames())
            {
                string childPath = subKeyPath + "\\" + child;
                lines.Add($"K|{childPath}");
                DumpRegistryTree(hive, childPath, lines);
            }
        }

        private static string EncodeRegValue(RegistryValueKind kind, object? val)
        {
            if (val == null) return "";
            try
            {
                if (kind == RegistryValueKind.Binary && val is byte[] bytes)
                    return Convert.ToBase64String(bytes);
                if (kind == RegistryValueKind.MultiString && val is string[] arr)
                    return string.Join("\u001e", arr);
                if (kind == RegistryValueKind.DWord || kind == RegistryValueKind.QWord)
                    return Convert.ToInt64(val).ToString();
                return val.ToString()?.Replace("\r", "").Replace("\n", " ") ?? "";
            }
            catch { return ""; }
        }

        private bool RestoreRegistryBackup(string backupFile, string label)
        {
            try
            {
                if (!File.Exists(backupFile)) return false;
                var lines = File.ReadAllLines(backupFile);
                if (lines.Length < 2 || lines[0] != "SOCRATESK12_REG_BACKUP_V1")
                {
                    // Legacy .reg from reg.exe
                    return RegImport(backupFile);
                }

                // header line 1: LABEL|rootPath
                string rootPath = lines[1].Contains("|") ? lines[1].Split('|')[1] : "";
                RegistryKey hive = label == "HKCU" || lines[1].StartsWith("HKCU")
                    ? Registry.CurrentUser
                    : Registry.LocalMachine;
                if (lines[1].StartsWith("HKCU")) hive = Registry.CurrentUser;
                if (lines[1].StartsWith("HKLM")) hive = Registry.LocalMachine;

                // Create keys first, then values
                foreach (string line in lines.Skip(2))
                {
                    if (line.StartsWith("K|"))
                    {
                        string path = line.Substring(2);
                        hive.CreateSubKey(path)?.Close();
                    }
                }
                foreach (string line in lines.Skip(2))
                {
                    if (!line.StartsWith("V|")) continue;
                    string[] parts = line.Split(new[] { '|' }, 5);
                    if (parts.Length < 5) continue;
                    string path = parts[1];
                    string name = parts[2];
                    if (name == "(default)") name = "";
                    int kindInt = int.Parse(parts[3]);
                    string encoded = parts[4];
                    var kind = (RegistryValueKind)kindInt;
                    using var key = hive.CreateSubKey(path);
                    if (key == null) continue;
                    object data = DecodeRegValue(kind, encoded);
                    key.SetValue(name, data, kind);
                }
                Log($"Restored {label} registry tree from backup.");
                return true;
            }
            catch (Exception ex)
            {
                Log($"Restore backup failed for {label}: {ex.Message}");
                return false;
            }
        }

        private static object DecodeRegValue(RegistryValueKind kind, string encoded)
        {
            if (kind == RegistryValueKind.Binary)
                return string.IsNullOrEmpty(encoded) ? Array.Empty<byte>() : Convert.FromBase64String(encoded);
            if (kind == RegistryValueKind.MultiString)
                return string.IsNullOrEmpty(encoded) ? Array.Empty<string>() : encoded.Split('\u001e');
            if (kind == RegistryValueKind.DWord)
                return unchecked((int)Convert.ToInt64(encoded));
            if (kind == RegistryValueKind.QWord)
                return Convert.ToInt64(encoded);
            return encoded ?? "";
        }

        private static bool RegImport(string regFile)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "reg.exe",
                    Arguments = $"import \"{regFile}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(30000);
                return p != null && p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private int DeleteRegistryTree(RegistryKey hive, string subKeyPath, string label)
        {
            try
            {
                using var existing = hive.OpenSubKey(subKeyPath, writable: false);
                if (existing == null)
                    return 0;
            }
            catch { return 0; }

            try
            {
                hive.DeleteSubKeyTree(subKeyPath, throwOnMissingSubKey: false);
                Log($"Deleted {label}\\{subKeyPath}");
                return 1;
            }
            catch (Exception ex)
            {
                Log($"Failed to delete {label}\\{subKeyPath}: {ex.Message}");
                return 0;
            }
        }

        private void PerformQuarantine()
        {
            Log("=== Starting Enable Tweak ===");

            var state = new QuarantineState
            {
                ProcessPath = ProcessPath,
                ProcessName = ProcessName,
                ServiceName = ServiceName,
                QuarantinedAt = DateTime.Now
            };

            // 1. Stop the process
            try
            {
                var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ProcessName));
                foreach (var p in procs)
                {
                    try
                    {
                        Log($"Stopping process PID {p.Id}...");
                        p.Kill(true);
                        p.WaitForExit(5000);
                        Log("Process stopped.");
                    }
                    catch (Exception ex)
                    {
                        Log($"Could not kill process {p.Id}: {ex.Message}");
                    }
                }
                if (procs.Length == 0) Log("Process was not running.");
            }
            catch (Exception ex)
            {
                Log("Process stop error: " + ex.Message);
            }

            // 2. Stop the service
            try
            {
                using var sc = new ServiceController(ServiceName);
                if (sc.Status != ServiceControllerStatus.Stopped &&
                    sc.Status != ServiceControllerStatus.StopPending)
                {
                    Log("Stopping service...");
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    Log("Service stopped.");
                }
                else
                {
                    Log("Service already stopped.");
                }
            }
            catch (Exception ex)
            {
                Log("Service stop: " + ex.Message);
            }

            // 3. Save original service config
            try
            {
                state.OriginalStartType = GetServiceStartType(ServiceName);
                state.OriginalImagePath = GetServiceImagePath(ServiceName);
                Log($"Original start type: {state.OriginalStartType}");
            }
            catch (Exception ex)
            {
                Log("Could not read service config: " + ex.Message);
            }

            // 4. Disable the service
            try
            {
                Log("Setting service start type to Disabled...");
                RunSc($"config \"{ServiceName}\" start= disabled");
                Log("Service disabled.");
            }
            catch (Exception ex)
            {
                Log("Failed to disable service: " + ex.Message);
            }

            // 5. Do NOT move or delete the program binary — only stop/disable
            state.ProcessPath = ProcessPath;
            Log("Program binary left in place (not deleted or moved).");

            // 6. IFEO block
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(
                    $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{ProcessName}");
                if (key != null)
                {
                    key.SetValue("Debugger", @"C:\Windows\System32\systray.exe_nonexistent", RegistryValueKind.String);
                    state.IfeoApplied = true;
                    Log("Extra protection applied.");
                }
            }
            catch (Exception ex)
            {
                Log("Extra protection failed (non-critical): " + ex.Message);
            }

            // 6b. Disable Run / startup entries for the program
            try
            {
                state.RemovedStartupEntries = DisableStartupEntries(ProcessName, ProcessPath);
                if (state.RemovedStartupEntries.Count > 0)
                    Log($"Disabled {state.RemovedStartupEntries.Count} startup entry(ies).");
                else
                    Log("No Run-key startup entries found for the program.");
            }
            catch (Exception ex)
            {
                Log("Startup disable: " + ex.Message);
            }

            // 7. Save state
            try
            {
                Directory.CreateDirectory(QuarantineRoot);
                string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(StateFilePath, json);
                Log("State saved.");
                SaveAppState();
            }
            catch (Exception ex)
            {
                Log("ERROR saving state: " + ex.Message);
            }

            Log("=== TWEAK ENABLED ===");
            Log("Tweak is active. Target will not start on reboot.");
        }

        // ===================== RESTORE =====================

        private async void BtnProgEnable_Click(object? sender, EventArgs e)
        {
            btnProgEnable.Enabled = false;
            btnProgDisable.Enabled = false;
            try
            {
                await Task.Run(() => PerformProgramsUnblock());
                RefreshAllStatuses(forceLog: false);
            }
            finally
            {
                btnProgEnable.Enabled = true;
                btnProgDisable.Enabled = true;
            }
        }

        private async void BtnProgDisable_Click(object? sender, EventArgs e)
        {
            btnProgEnable.Enabled = false;
            btnProgDisable.Enabled = false;
            try
            {
                await Task.Run(() => PerformProgramsRestore());
                if (chkProgMonitor != null && chkProgMonitor.Checked) chkProgMonitor.Checked = false;
                RefreshAllStatuses(forceLog: false);
            }
            finally
            {
                btnProgEnable.Enabled = true;
                btnProgDisable.Enabled = true;
            }
        }

        private void PerformProgramsUnblock()
        {
            Log("=== Unblock Blocked Programs (Enable) ===");
            var saved = new List<SavedPolicyValue>();

            foreach (var (hive, hiveName, subKey) in new (RegistryKey, string, string)[]
            {
                (Registry.CurrentUser, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer"),
                (Registry.LocalMachine, "HKLM", @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer"),
                (Registry.LocalMachine, "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer"),
            })
            {
                DisableDwordPolicy(hive, hiveName, subKey, "DisallowRun", saved);
                DisableDwordPolicy(hive, hiveName, subKey, "RestrictRun", saved);
            }

            SetDwordPolicyIfBlocking(
                Registry.LocalMachine, "HKLM",
                @"SOFTWARE\Policies\Microsoft\Windows\Safer\CodeIdentifiers",
                "DefaultLevel",
                new[] { 0, 131072 },
                262144,
                saved);

            foreach (string store in new[] { "Exe", "Dll", "Script", "Msi", "Appx" })
            {
                SetDwordPolicyIfBlocking(
                    Registry.LocalMachine, "HKLM",
                    @"SOFTWARE\Policies\Microsoft\Windows\SrpV2\" + store,
                    "EnforcementMode",
                    new[] { 1 },
                    0,
                    saved);
            }

            try
            {
                Directory.CreateDirectory(QuarantineRoot);
                string json = JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ProgramsStatePath, json);
                Log($"Saved {saved.Count} policy value(s) for restore. Restart-resistant.");
                SaveAppState();
            }
            catch (Exception ex)
            {
                Log("Failed to save programs state: " + ex.Message);
            }

            Log("Program-blocking policies disabled (values changed only — nothing deleted).");
            Log("Works without Policy Editor. === DONE ===");
        }

        private void PerformProgramsRestore()
        {
            Log("=== Unblock Blocked Programs (Disable / Restore) ===");
            if (!File.Exists(ProgramsStatePath))
            {
                Log("No saved policy state — nothing to restore.");
                Log("=== DONE ===");
                return;
            }

            try
            {
                string json = File.ReadAllText(ProgramsStatePath);
                var saved = JsonSerializer.Deserialize<List<SavedPolicyValue>>(json) ?? new List<SavedPolicyValue>();
                foreach (var entry in saved)
                {
                    try
                    {
                        RegistryKey hive = entry.Hive == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
                        if (!entry.Existed)
                        {
                            using var key = hive.OpenSubKey(entry.KeyPath, writable: true);
                            key?.DeleteValue(entry.ValueName, throwOnMissingValue: false);
                            Log($"Restored (removed added value): {entry.Hive}\\{entry.KeyPath}\\{entry.ValueName}");
                        }
                        else
                        {
                            using var key = hive.CreateSubKey(entry.KeyPath);
                            if (key == null) continue;
                            key.SetValue(entry.ValueName, entry.DwordValue, RegistryValueKind.DWord);
                            Log($"Restored: {entry.Hive}\\{entry.KeyPath}\\{entry.ValueName} = {entry.DwordValue}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Restore failed for {entry.ValueName}: {ex.Message}");
                    }
                }

                File.Delete(ProgramsStatePath);
                Log("Original policy values restored. State cleared.");
            }
            catch (Exception ex)
            {
                Log("Restore error: " + ex.Message);
            }
            SaveAppState();
            Log("=== DONE ===");
            TryRemoveTraceFolder();
        }

        private void DisableDwordPolicy(RegistryKey hive, string hiveName, string subKey, string valueName, List<SavedPolicyValue> saved)
        {
            try
            {
                using var key = hive.OpenSubKey(subKey, writable: true);
                if (key == null) return;
                object? raw = key.GetValue(valueName);
                if (raw == null) return;
                int current = Convert.ToInt32(raw);
                if (current == 0) return;

                saved.Add(new SavedPolicyValue
                {
                    Hive = hiveName,
                    KeyPath = subKey,
                    ValueName = valueName,
                    Existed = true,
                    ValueKind = (int)RegistryValueKind.DWord,
                    DwordValue = current
                });
                key.SetValue(valueName, 0, RegistryValueKind.DWord);
                Log($"Disabled {hiveName}\\{subKey}\\{valueName} ({current} → 0)");
            }
            catch (Exception ex)
            {
                Log($"Skip {valueName}: {ex.Message}");
            }
        }

        private void SetDwordPolicyIfBlocking(
            RegistryKey hive, string hiveName, string subKey, string valueName,
            int[] blockingValues, int safeValue, List<SavedPolicyValue> saved)
        {
            try
            {
                using var keyRead = hive.OpenSubKey(subKey, writable: false);
                if (keyRead == null) return;
                object? raw = keyRead.GetValue(valueName);
                if (raw == null) return;
                int current = Convert.ToInt32(raw);
                if (Array.IndexOf(blockingValues, current) < 0) return;

                saved.Add(new SavedPolicyValue
                {
                    Hive = hiveName,
                    KeyPath = subKey,
                    ValueName = valueName,
                    Existed = true,
                    ValueKind = (int)RegistryValueKind.DWord,
                    DwordValue = current
                });

                using var key = hive.OpenSubKey(subKey, writable: true);
                key?.SetValue(valueName, safeValue, RegistryValueKind.DWord);
                Log($"Set {hiveName}\\{subKey}\\{valueName} ({current} → {safeValue})");
            }
            catch (Exception ex)
            {
                Log($"Skip {valueName}: {ex.Message}");
            }
        }

        private bool ReapplyProgramsUnblock()
        {
            if (!File.Exists(ProgramsStatePath)) return false;
            bool acted = false;

            foreach (var (hive, subKey) in new (RegistryKey, string)[]
            {
                (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer"),
                (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer"),
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer"),
            })
            {
                try
                {
                    using var key = hive.OpenSubKey(subKey, writable: true);
                    if (key == null) continue;
                    foreach (string name in new[] { "DisallowRun", "RestrictRun" })
                    {
                        object? raw = key.GetValue(name);
                        if (raw != null && Convert.ToInt32(raw) != 0)
                        {
                            key.SetValue(name, 0, RegistryValueKind.DWord);
                            acted = true;
                        }
                    }
                }
                catch { }
            }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Policies\Microsoft\Windows\Safer\CodeIdentifiers", writable: true);
                if (key != null)
                {
                    object? raw = key.GetValue("DefaultLevel");
                    if (raw != null)
                    {
                        int v = Convert.ToInt32(raw);
                        if (v == 0 || v == 131072)
                        {
                            key.SetValue("DefaultLevel", 262144, RegistryValueKind.DWord);
                            acted = true;
                        }
                    }
                }
            }
            catch { }

            foreach (string store in new[] { "Exe", "Dll", "Script", "Msi", "Appx" })
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\Policies\Microsoft\Windows\SrpV2\" + store, writable: true);
                    if (key == null) continue;
                    object? raw = key.GetValue("EnforcementMode");
                    if (raw != null && Convert.ToInt32(raw) == 1)
                    {
                        key.SetValue("EnforcementMode", 0, RegistryValueKind.DWord);
                        acted = true;
                    }
                }
                catch { }
            }
            return acted;
        }

        private async void BtnRestore_Click(object? sender, EventArgs e)
        {
            btnQuarantine.Enabled = false;
            btnRestore.Enabled = false;
            SetBadge("Working…", Color.FromArgb(180, 140, 40));
            try
            {
                await Task.Run(() => PerformRestore());
                SetBadge("Disabled", Danger);
                // Filter tweak off — turn off its background monitor
                if (chkMonitor.Checked) chkMonitor.Checked = false;
                RefreshAllStatuses(forceLog: false);
            }
            finally
            {
                btnQuarantine.Enabled = true;
                btnRestore.Enabled = true;
            }
        }

        private void PerformRestore()
        {
            Log("=== Starting Disable Tweak ===");

            QuarantineState? state = null;
            if (File.Exists(StateFilePath))
            {
                try
                {
                    string json = File.ReadAllText(StateFilePath);
                    state = JsonSerializer.Deserialize<QuarantineState>(json);
                }
                catch (Exception ex)
                {
                    Log("Could not read state file: " + ex.Message);
                }
            }

            string processPath = state?.ProcessPath ?? ProcessPath;
            string? quarantinedPath = state?.QuarantinedPath;

            // 1. Remove IFEO
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{ProcessName}", true);
                if (key != null)
                {
                    key.DeleteValue("Debugger", false);
                    Log("Extra protection removed.");
                }
            }
            catch (Exception ex)
            {
                Log("Cleanup: " + ex.Message);
            }

            // 1b. Restore startup entries
            try
            {
                if (state?.RemovedStartupEntries != null && state.RemovedStartupEntries.Count > 0)
                {
                    RestoreStartupEntries(state.RemovedStartupEntries);
                    Log($"Restored {state.RemovedStartupEntries.Count} startup entry(ies).");
                }
                else
                {
                    Log("No saved startup entries to restore.");
                }
            }
            catch (Exception ex)
            {
                Log("Startup restore: " + ex.Message);
            }

            // 2. Binary was never moved — nothing to restore on disk
            Log("Program binary was never moved; nothing to restore on disk.");

            // 3. Set service to Automatic
            try
            {
                Log("Setting service start type to Automatic...");
                RunSc($"config \"{ServiceName}\" start= auto");
                Log("Service set to Automatic.");
            }
            catch (Exception ex)
            {
                Log("Failed to set start type: " + ex.Message);
            }

            // 4. Start service
            try
            {
                using var sc = new ServiceController(ServiceName);
                if (sc.Status != ServiceControllerStatus.Running)
                {
                    Log("Starting service...");
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                    Log("Service started.");
                }
                else
                {
                    Log("Service already running.");
                }
            }
            catch (Exception ex)
            {
                Log("Service start: " + ex.Message);
            }

            // 5. Start process if needed
            try
            {
                if (File.Exists(processPath))
                {
                    var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ProcessName));
                    if (running.Length == 0)
                    {
                        Log("Starting process...");
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = processPath,
                            UseShellExecute = true
                        });
                        Log("Process start requested.");
                    }
                    else
                    {
                        Log("Process already running.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Process start (non-critical): " + ex.Message);
            }

            // 6. Clear state
            try
            {
                if (File.Exists(StateFilePath))
                    File.Delete(StateFilePath);
                Log("State cleared.");
            }
            catch { }

            SaveAppState();
            Log("=== TWEAK DISABLED ===");
            Log("Tweak disabled. Service set to start with Windows.");
            TryRemoveTraceFolder();
        }

        // ===================== WARP / WiFi Network Tweak =====================

        private const string WarpMsiUrl =
            "https://1111-releases.cloudflareclient.com/windows/Cloudflare_WARP_Release-x64.msi";

        private static string? FindWarpCli()
        {
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Cloudflare", "Cloudflare WARP", "warp-cli.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Cloudflare", "Cloudflare WARP", "warp-cli.exe"),
            };
            foreach (string c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }

        private bool IsWarpInstalled()
        {
            // Source of truth: warp-cli on disk (flag alone is not enough if user removed WARP)
            return FindWarpCli() != null;
        }

        private bool IsWarpConnected()
        {
            try
            {
                string? cli = FindWarpCli();
                if (cli == null) return false;
                string output = RunWarpCli(cli, "status");
                if (string.IsNullOrWhiteSpace(output) || output.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                    return false;
                string lower = output.ToLowerInvariant();
                // Prefer explicit status lines (avoid substring traps)
                if (lower.Contains("disconnected") || lower.Contains("not connected"))
                    return false;
                return lower.Contains("status: connected")
                    || lower.Contains("status: connecting")
                    || (lower.Contains("connected") && !lower.Contains("disconnect"));
            }
            catch { return false; }
        }

        private string RunWarpCli(string cli, string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = cli,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) return "";
                var stdoutTask = p.StandardOutput.ReadToEndAsync();
                var stderrTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(45000))
                {
                    try { p.Kill(true); } catch { }
                    return "ERROR: warp-cli timed out.";
                }
                string stdout = stdoutTask.GetAwaiter().GetResult();
                string stderr = stderrTask.GetAwaiter().GetResult();
                return (stdout + "\n" + stderr).Trim();
            }
            catch (Exception ex)
            {
                return "ERROR: warp-cli failed: " + ex.Message;
            }
        }

        private async void BtnWarpInstall_Click(object? sender, EventArgs e)
        {
            btnWarpInstall.Enabled = false;
            try
            {
                RefreshAllStatuses(forceLog: true);

                if (!IsProgramsTweakEnabled())
                {
                    Log("ERROR: Install VPN requires \"Unblock Blocked Programs\" to be Enabled first.");
                    Log("Enable that tweak, then try Install VPN again.");
                    MessageBox.Show(
                        "Install VPN requires the \"Unblock Blocked Programs\" tweak to be Enabled.\n\n" +
                        "Enable it first, then click Install VPN again.",
                        "Requirement not met",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                if (FindWarpCli() != null)
                {
                    Log("WARP is already installed. Install VPN is one-time only after a successful install.");
                    try
                    {
                        Directory.CreateDirectory(QuarantineRoot);
                        File.WriteAllText(WarpInstalledFlag, DateTime.Now.ToString("o"));
                    }
                    catch { }
                    RefreshAllStatuses(forceLog: false);
                    return;
                }

                await Task.Run(() => PerformWarpInstall());
                RefreshAllStatuses(forceLog: false);
            }
            finally
            {
                btnWarpInstall.Enabled = FindWarpCli() == null;
            }
        }

        private void PerformWarpInstall()
        {
            Log("=== Install VPN (Cloudflare WARP) ===");
            try
            {
                Directory.CreateDirectory(QuarantineRoot);
                string msiPath = Path.Combine(Path.GetTempPath(), "Cloudflare_WARP_Release-x64.msi");

                Log("Downloading WARP installer...");
                using (var wc = new System.Net.Http.HttpClient())
                {
                    wc.Timeout = TimeSpan.FromMinutes(5);
                    byte[] data = wc.GetByteArrayAsync(WarpMsiUrl).GetAwaiter().GetResult();
                    File.WriteAllBytes(msiPath, data);
                    Log($"Downloaded {data.Length} bytes.");
                }

                Log("Running silent install (msiexec)...");
                var psi = new ProcessStartInfo
                {
                    FileName = "msiexec.exe",
                    Arguments = $"/i \"{msiPath}\" /quiet /norestart",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    p?.WaitForExit(600000);
                    int code = p?.ExitCode ?? -1;
                    Log($"msiexec exit code: {code}");
                    if (code != 0)
                    {
                        Log("ERROR: WARP install failed. See exit code above.");
                        return;
                    }
                }

                string? cli = null;
                for (int i = 0; i < 30; i++)
                {
                    cli = FindWarpCli();
                    if (cli != null) break;
                    System.Threading.Thread.Sleep(1000);
                }

                if (cli == null)
                {
                    Log("ERROR: WARP install finished but warp-cli.exe was not found.");
                    return;
                }

                Log("WARP installed. Attempting registration (consumer mode)...");
                try
                {
                    string regOut = RunWarpCli(cli, "registration new");
                    if (!string.IsNullOrWhiteSpace(regOut))
                        Log("registration: " + regOut);
                }
                catch (Exception ex)
                {
                    Log("registration note: " + ex.Message);
                }

                File.WriteAllText(WarpInstalledFlag, DateTime.Now.ToString("o"));
                Log("Install VPN complete (one-time). WARP will not be uninstalled by this program.");
                Log("=== DONE ===");
            }
            catch (Exception ex)
            {
                Log("ERROR: WARP install failed: " + ex.Message);
            }
        }

        private async void BtnWarpEnable_Click(object? sender, EventArgs e)
        {
            btnWarpEnable.Enabled = false;
            btnWarpDisable.Enabled = false;
            try
            {
                await Task.Run(() =>
                {
                    Log("=== WiFi Network Tweak: Enable (start WARP tunnel) ===");
                    _warpStatusCacheAt = DateTime.MinValue;
                    string? cli = FindWarpCli();
                    if (cli == null)
                    {
                        Log("ERROR: WARP is not installed. Use Install VPN first.");
                        return;
                    }
                    try
                    {
                        string outConnect = RunWarpCli(cli, "connect");
                        if (!string.IsNullOrWhiteSpace(outConnect))
                            Log(outConnect);
                        string status = RunWarpCli(cli, "status");
                        Log("status: " + status);
                        if (!IsWarpConnected())
                            Log("ERROR: WARP did not report Connected. Check the status output above.");
                        else
                            Log("WARP tunnel started.");
                    }
                    catch (Exception ex)
                    {
                        Log("ERROR: Failed to start WARP: " + ex.Message);
                    }
                    Log("=== DONE ===");
                });
                RefreshAllStatuses(forceLog: false);
            }
            finally
            {
                btnWarpEnable.Enabled = true;
                btnWarpDisable.Enabled = true;
            }
        }

        private async void BtnWarpDisable_Click(object? sender, EventArgs e)
        {
            btnWarpEnable.Enabled = false;
            btnWarpDisable.Enabled = false;
            try
            {
                await Task.Run(() => PerformWarpDisconnect());
                RefreshAllStatuses(forceLog: false);
            }
            finally
            {
                btnWarpEnable.Enabled = true;
                btnWarpDisable.Enabled = true;
            }
        }

        private void PerformWarpDisconnect()
        {
            Log("=== WiFi Network Tweak: Disable (stop WARP tunnel) ===");
            _warpStatusCacheAt = DateTime.MinValue;
            string? cli = FindWarpCli();
            if (cli == null)
            {
                Log("WARP not installed — nothing to disconnect.");
                Log("=== DONE ===");
                return;
            }
            try
            {
                string outDisc = RunWarpCli(cli, "disconnect");
                if (!string.IsNullOrWhiteSpace(outDisc))
                    Log(outDisc);
                string status = RunWarpCli(cli, "status");
                Log("status: " + status);
                Log("WARP tunnel stopped. WARP remains installed.");
            }
            catch (Exception ex)
            {
                Log("ERROR: Failed to stop WARP: " + ex.Message);
            }
            Log("=== DONE ===");
        }

        private string GetWarpStatusText()
        {
            // Short cache so tray right-click stays responsive (same facts, less spam)
            if ((DateTime.UtcNow - _warpStatusCacheAt).TotalSeconds < 8
                && !string.IsNullOrEmpty(_warpStatusCache))
                return _warpStatusCache;

            string result;
            if (!IsWarpInstalled())
                result = "WARP: Not installed";
            else if (IsWarpConnected())
                result = "WARP: Connected";
            else
                result = "WARP: Installed (disconnected)";

            _warpStatusCache = result;
            _warpStatusCacheAt = DateTime.UtcNow;
            return result;
        }

        private async void BtnDisableAll_Click(object? sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "This will:\n\n" +
                "• Disable all tweaks and restore original settings\n" +
                "• Stop all background tasks\n" +
                "• Delete SocratesK12 data/backups only after restore\n" +
                "• Close the program\n\n" +
                "Continue?",
                "DISABLE ALL AND CLOSE",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result != DialogResult.Yes)
                return;

            btnDisableAll.Enabled = false;
            btnRefresh.Enabled = false;
            try
            {
                // Stop background first so monitors cannot race restore
                if (chkMonitor != null) chkMonitor.Checked = false;
                if (chkExtMonitor != null) chkExtMonitor.Checked = false;
                if (chkProgMonitor != null) chkProgMonitor.Checked = false;
                StopMonitoring();

                await Task.Run(() =>
                {
                    if (IsFilterTweakEnabled())
                    {
                        Log("Disable All: restoring network filter...");
                        PerformRestore();
                    }
                    if (IsExtensionsTweakEnabled())
                    {
                        Log("Disable All: restoring extensions policies...");
                        PerformRestoreExtensions();
                    }
                    if (IsProgramsTweakEnabled())
                    {
                        Log("Disable All: restoring program-blocking policies...");
                        PerformProgramsRestore();
                    }

                    // Stop WARP tunnel only — never uninstall
                    Log("Disable All: disconnecting WARP tunnel (leaving WARP installed)...");
                    PerformWarpDisconnect();

                    // Final trace removal after all restores
                    ForceRemoveTraceFolder();
                });

                if (!Directory.Exists(QuarantineRoot))
                    Log("All tweaks disabled. Data folder removed. Closing...");
                else
                    Log("All tweaks disabled. Data folder kept (backup/state still needed or cleanup failed). Closing...");
            }
            catch (Exception ex)
            {
                Log("Disable All error: " + ex.Message);
            }
            finally
            {
                // Force exit even if form wants to stay for tray
                _isMonitoring = false;
                try { SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch; } catch { }
                _statusTimer?.Stop();
                _trayIcon?.Dispose();
                _trayIcon = null;
                Application.Exit();
            }
        }

        /// <summary>
        /// True if any tweak still needs %ProgramData%\SocratesK12
        /// (filter state, extension .reg backups, or programs policy state).
        /// </summary>
        private bool AnyTweakUsingDataFolder()
        {
            // Backups alone mean we must keep the folder
            return IsFilterTweakEnabled()
                || IsExtensionsTweakEnabled()
                || IsProgramsTweakEnabled()
                || HasExtensionBackups();
        }

        /// <summary>
        /// Removes the shared data folder only when EVERY tweak that uses it is disabled.
        /// Disabling a single backup-based tweak must NOT wipe other tweaks' backups/state.
        /// </summary>
        private void TryRemoveTraceFolder()
        {
            try
            {
                if (AnyTweakUsingDataFolder())
                {
                    Log("Data folder kept — at least one tweak still needs backups/state.");
                    return;
                }

                ForceRemoveTraceFolder();
            }
            catch (Exception ex)
            {
                Log("Cleanup: " + ex.Message);
            }
        }

        /// <summary>
        /// Deletes the SocratesK12 data folder. Caller must ensure no tweak still needs it
        /// (or is intentionally wiping everything after full restore, e.g. Disable All).
        /// </summary>
        private void ForceRemoveTraceFolder()
        {
            try
            {
                // Hard safety: never wipe while a tweak is enabled OR extension backups remain
                if (AnyTweakUsingDataFolder() || HasExtensionBackups())
                {
                    Log("Skip folder delete — enabled tweak and/or extension backups still present.");
                    return;
                }

                if (!Directory.Exists(QuarantineRoot))
                {
                    Log("No data folder to remove.");
                    return;
                }

                foreach (string f in new[]
                {
                    StateFilePath,
                    ExtensionsFlagPath,
                    ExtensionsHkcuBackup,
                    ExtensionsHklmBackup,
                    ProgramsStatePath
                })
                {
                    try
                    {
                        if (File.Exists(f))
                            File.Delete(f);
                    }
                    catch { }
                }

                try
                {
                    Directory.Delete(QuarantineRoot, recursive: true);
                    Log("Removed data folder (all backup-using tweaks disabled): " + QuarantineRoot);
                }
                catch (Exception ex)
                {
                    try
                    {
                        foreach (string f in Directory.GetFiles(QuarantineRoot, "*", SearchOption.AllDirectories))
                        {
                            try { File.Delete(f); } catch { }
                        }
                        Directory.Delete(QuarantineRoot, recursive: true);
                        Log("Removed data folder after cleanup: " + QuarantineRoot);
                    }
                    catch
                    {
                        Log("Could not fully remove data folder: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Force cleanup: " + ex.Message);
            }
        }

        // ===================== BACKGROUND MONITOR =====================

        private void ChkMonitor_CheckedChanged(object? sender, EventArgs e)
        {
            if (chkMonitor.Checked && !IsFilterTweakEnabled())
            {
                chkMonitor.Checked = false;
                Log("Enable the Network Filter tweak before turning on background monitoring.");
                return;
            }
            SyncBackgroundWorkers();
        }

        private void ChkExtMonitor_CheckedChanged(object? sender, EventArgs e)
        {
            if (chkExtMonitor.Checked && !IsExtensionsTweakEnabled())
            {
                chkExtMonitor.Checked = false;
                Log("Enable the Extensions tweak before turning on registry watch.");
                return;
            }
            SyncBackgroundWorkers();
        }

        private void ChkProgMonitor_CheckedChanged(object? sender, EventArgs e)
        {
            if (chkProgMonitor.Checked && !IsProgramsTweakEnabled())
            {
                chkProgMonitor.Checked = false;
                Log("Enable the Programs tweak before turning on registry watch.");
                return;
            }
            SyncBackgroundWorkers();
        }

        /// <summary>
        /// Filter: process-start + service/IFEO notify. Extensions/Programs: registry notify.
        /// </summary>
        private void SyncBackgroundWorkers()
        {
            bool any = AnyBackgroundEnabled();
            if (any)
            {
                if (!_isMonitoring)
                {
                    _isMonitoring = true;
                    EnsureTrayIcon();
                    Log("Background protection active (tray).");
                }
                UpdateTrayTooltip();
            }

            // Filter — process start (WMI) + service/IFEO registry notify + slow backup timer
            if (chkMonitor?.Checked == true && IsFilterTweakEnabled())
                StartFilterEventWatch();
            else
                StopFilterEventWatch();

            // Extensions — registry watch
            if (chkExtMonitor?.Checked == true && IsExtensionsTweakEnabled())
                StartExtRegistryWatch();
            else
                StopExtRegistryWatch();

            // Programs — registry watch
            if (chkProgMonitor?.Checked == true && IsProgramsTweakEnabled())
                StartProgRegistryWatch();
            else
                StopProgRegistryWatch();

            if (!AnyBackgroundEnabled())
            {
                _isMonitoring = false;
                if (_trayIcon != null) _trayIcon.Visible = false;
                Log("Background protection stopped.");
            }
        }

        private void StartExtRegistryWatch()
        {
            if (_extWatchCts != null) return;
            _extWatchCts = new CancellationTokenSource();
            var token = _extWatchCts.Token;
            Task.Run(() => RegistryWatchLoop(
                token,
                "Extensions",
                () => IsExtensionsTweakEnabled() && chkExtMonitor?.Checked == true,
                () =>
                {
                    int n = 0;
                    n += DeleteRegistryTree(Registry.LocalMachine, @"SOFTWARE\Policies\Google", "HKLM");
                    n += DeleteRegistryTree(Registry.CurrentUser, @"Software\Policies\Google", "HKCU");
                    return n > 0;
                },
                new (RegistryHive hive, string path)[]
                {
                    (RegistryHive.LocalMachine, @"SOFTWARE\Policies"),
                    (RegistryHive.CurrentUser, @"Software\Policies"),
                }), token);
            Log("Extensions: registry watch started (reacts when policies are rewritten).");
        }

        private void StopExtRegistryWatch()
        {
            if (_extWatchCts == null) return;
            try { _extWatchCts.Cancel(); } catch { }
            try { _extWatchCts.Dispose(); } catch { }
            _extWatchCts = null;
            Log("Extensions: registry watch stopped.");
        }

        private void StartProgRegistryWatch()
        {
            if (_progWatchCts != null) return;
            _progWatchCts = new CancellationTokenSource();
            var token = _progWatchCts.Token;
            Task.Run(() => RegistryWatchLoop(
                token,
                "Programs",
                () => IsProgramsTweakEnabled() && chkProgMonitor?.Checked == true,
                () => ReapplyProgramsUnblock(),
                new (RegistryHive hive, string path)[]
                {
                    (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies"),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies"),
                    (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows"),
                }), token);
            Log("Programs: registry watch started (reacts when policies are rewritten).");
        }

        private void StopProgRegistryWatch()
        {
            if (_progWatchCts == null) return;
            try { _progWatchCts.Cancel(); } catch { }
            try { _progWatchCts.Dispose(); } catch { }
            _progWatchCts = null;
            Log("Programs: registry watch stopped.");
        }

        private void RegistryWatchLoop(
            CancellationToken token,
            string name,
            Func<bool> stillWanted,
            Func<bool> reinforceIfNeeded,
            (RegistryHive hive, string path)[] watchTargets)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!stillWanted())
                        break;
                    if (_sessionLocked)
                    {
                        Thread.Sleep(1000);
                        continue;
                    }

                    bool waited = false;
                    foreach (var (hive, subPath) in watchTargets)
                    {
                        if (token.IsCancellationRequested || !stillWanted()) break;
                        if (_sessionLocked) break;

                        using var baseKey = hive == RegistryHive.LocalMachine
                            ? RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default)
                            : RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
                        using var key = baseKey.OpenSubKey(subPath, writable: false);
                        if (key == null)
                        {
                            Thread.Sleep(500);
                            continue;
                        }

                        waited = true;
                        // Async notify + wait with timeout so Cancel can take effect
                        using var evt = new ManualResetEvent(false);
                        int filter = REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET;
                        int rc = RegNotifyChangeKeyValue(
                            key.Handle.DangerousGetHandle(),
                            true,
                            filter,
                            evt.SafeWaitHandle.DangerousGetHandle(),
                            true);
                        if (rc != 0)
                        {
                            Thread.Sleep(1000);
                            continue;
                        }

                        // Wait up to 5s, then re-check cancellation / stillWanted
                        while (!token.IsCancellationRequested && stillWanted())
                        {
                            if (evt.WaitOne(1000))
                                break;
                            if (_sessionLocked)
                                break;
                        }

                        if (token.IsCancellationRequested || !stillWanted() || _sessionLocked)
                            break;

                        // Debounce burst writes from our own reinforce
                        Thread.Sleep(400);
                        if (!stillWanted() || _sessionLocked) continue;

                        try
                        {
                            bool changed = reinforceIfNeeded();
                            if (changed)
                                Log($"[Watch:{name}] Registry change — policies corrected.");
                        }
                        catch (Exception ex)
                        {
                            Log($"[Watch:{name}] Reinforce error: " + ex.Message);
                        }

                        // Cool-down so our own writes don't immediately re-trigger a tight loop
                        Thread.Sleep(1500);
                        break;
                    }

                    if (!waited)
                        Thread.Sleep(1500);
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) break;
                    Log($"[Watch:{name}] " + ex.Message);
                    Thread.Sleep(2000);
                }
            }
        }

        private void StartMonitoring() => SyncBackgroundWorkers();

        private void StopMonitoring()
        {
            if (chkMonitor != null) chkMonitor.Checked = false;
            if (chkExtMonitor != null) chkExtMonitor.Checked = false;
            if (chkProgMonitor != null) chkProgMonitor.Checked = false;
            StopFilterEventWatch();
            StopExtRegistryWatch();
            StopProgRegistryWatch();
            _isMonitoring = false;
            if (_trayIcon != null) _trayIcon.Visible = false;
        }

        private bool EnforceFilterQuarantine(string reason, bool forceLog = false)
        {
            if (_sessionLocked) return false;
            if (chkMonitor?.Checked != true || !IsFilterTweakEnabled()) return false;
            bool acted = false;
            try
            {
                string currentStart = GetServiceStartType(ServiceName);
                if (!string.Equals(currentStart, "Disabled", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(currentStart, "4", StringComparison.OrdinalIgnoreCase)
                    && FilterServiceExists())
                {
                    RunSc($"config \"{ServiceName}\" start= disabled");
                    acted = true;
                }
                try
                {
                    using var sc = new ServiceController(ServiceName);
                    if (sc.Status != ServiceControllerStatus.Stopped
                        && sc.Status != ServiceControllerStatus.StopPending)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(8));
                        acted = true;
                    }
                }
                catch { }

                string proc = Path.GetFileNameWithoutExtension(ProcessName);
                foreach (var p in Process.GetProcessesByName(proc))
                {
                    try { p.Kill(true); acted = true; } catch { }
                }

                if (File.Exists(ProcessPath) && !IsFilterProcessStartupBlocked())
                {
                    try
                    {
                        using var key = Registry.LocalMachine.CreateSubKey(
                            $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{ProcessName}");
                        key?.SetValue("Debugger", @"C:\Windows\System32\systray.exe_nonexistent", RegistryValueKind.String);
                        acted = true;
                    }
                    catch { }
                }

                if (acted || forceLog)
                    Log("[Filter] " + reason);
            }
            catch (Exception ex)
            {
                Log("[Filter] " + ex.Message);
            }
            return acted;
        }

        private void StartFilterEventWatch()
        {
            // Process start via WMI
            if (_filterProcessWatcher == null)
            {
                try
                {
                    string q = "SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName = '" + ProcessName + "'";
                    _filterProcessWatcher = new ManagementEventWatcher(new WqlEventQuery(q));
                    _filterProcessWatcher.EventArrived += (s, e) =>
                    {
                        try
                        {
                            if (_sessionLocked) return;
                            EnforceFilterQuarantine("Process start detected — stopped and re-quarantined.", forceLog: true);
                        }
                        catch { }
                    };
                    _filterProcessWatcher.Start();
                    Log("Filter: process-start watcher active.");
                }
                catch (Exception ex)
                {
                    Log("Filter: process watcher failed (will rely on service/IFEO watch + backup): " + ex.Message);
                }
            }

            // Service + IFEO registry notify
            if (_filterWatchCts == null)
            {
                _filterWatchCts = new CancellationTokenSource();
                var token = _filterWatchCts.Token;
                Task.Run(() => RegistryWatchLoop(
                    token,
                    "Filter",
                    () => IsFilterTweakEnabled() && chkMonitor?.Checked == true,
                    () => EnforceFilterQuarantine("Service/IFEO registry change — re-quarantined."),
                    new (RegistryHive hive, string path)[]
                    {
                        (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\" + ServiceName),
                        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options"),
                    }), token);
                Log("Filter: service + IFEO registry watch started.");
            }

            // Slow backup poll (90s) in case a notification is missed
            if (_monitorTimer == null)
            {
                _monitorTimer = new System.Windows.Forms.Timer { Interval = 90_000 };
                _monitorTimer.Tick += (s, e) =>
                {
                    if (_sessionLocked) return;
                    if (chkMonitor?.Checked == true && IsFilterTweakEnabled())
                        EnforceFilterQuarantine("Backup check — re-quarantined.", forceLog: false);
                };
                _monitorTimer.Start();
            }
        }

        private void StopFilterEventWatch()
        {
            if (_filterProcessWatcher != null)
            {
                try { _filterProcessWatcher.Stop(); } catch { }
                try { _filterProcessWatcher.Dispose(); } catch { }
                _filterProcessWatcher = null;
            }
            if (_filterWatchCts != null)
            {
                try { _filterWatchCts.Cancel(); } catch { }
                try { _filterWatchCts.Dispose(); } catch { }
                _filterWatchCts = null;
            }
            if (_monitorTimer != null)
            {
                _monitorTimer.Stop();
                _monitorTimer.Dispose();
                _monitorTimer = null;
            }
        }

        private class AppPersistedState
        {
            public bool FilterEnabled { get; set; }
            public bool ExtensionsEnabled { get; set; }
            public bool ProgramsEnabled { get; set; }
        }

        private void SaveAppState()
        {
            try
            {
                Directory.CreateDirectory(QuarantineRoot);
                var state = new AppPersistedState
                {
                    FilterEnabled = File.Exists(StateFilePath),
                    ExtensionsEnabled = File.Exists(ExtensionsFlagPath) || HasExtensionBackups(),
                    ProgramsEnabled = File.Exists(ProgramsStatePath)
                };
                File.WriteAllText(AppStatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Log("App state save: " + ex.Message);
            }
        }

        private bool HasExtensionBackups()
        {
            try
            {
                return (File.Exists(ExtensionsHklmBackup) && new FileInfo(ExtensionsHklmBackup).Length > 0)
                    || (File.Exists(ExtensionsHkcuBackup) && new FileInfo(ExtensionsHkcuBackup).Length > 0);
            }
            catch { return false; }
        }

        /// <summary>
        /// On startup: if backups exist from last session, recover enabled state
        /// so we never treat active backups as "disabled" and delete them.
        /// </summary>
        private void ReconcilePersistedState()
        {
            try
            {
                Directory.CreateDirectory(QuarantineRoot);

                if (HasExtensionBackups() && !File.Exists(ExtensionsFlagPath))
                {
                    File.WriteAllText(ExtensionsFlagPath, DateTime.Now.ToString("o"));
                    Log("Recovered Extensions ENABLED from existing backups (flag was missing).");
                }

                if (File.Exists(AppStatePath))
                {
                    try
                    {
                        var prev = JsonSerializer.Deserialize<AppPersistedState>(File.ReadAllText(AppStatePath));
                        if (prev != null)
                        {
                            Log($"Last session — Filter:{(prev.FilterEnabled ? "ON" : "OFF")}, Extensions:{(prev.ExtensionsEnabled ? "ON" : "OFF")}, Programs:{(prev.ProgramsEnabled ? "ON" : "OFF")}");
                        }
                    }
                    catch { }
                }

                SaveAppState();
            }
            catch (Exception ex)
            {
                Log("State reconcile: " + ex.Message);
            }
        }

        /// <summary>
        /// Re-apply enabled tweaks. Only from: Enable actions, Refresh Status (forceLog),
        /// and the background watch checkboxes — not silent 5s UI polls alone.
        /// </summary>
        private void ReinforceEnabledTweaksIfNeeded(bool log)
        {
            try
            {
                if (IsFilterTweakEnabled())
                {
                    try
                    {
                        RunSc($"config \"{ServiceName}\" start= disabled");
                        try
                        {
                            using var sc = new ServiceController(ServiceName);
                            if (sc.Status != ServiceControllerStatus.Stopped
                                && sc.Status != ServiceControllerStatus.StopPending)
                            {
                                sc.Stop();
                                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(8));
                            }
                        }
                        catch { }
                        string proc = Path.GetFileNameWithoutExtension(ProcessName);
                        foreach (var p in Process.GetProcessesByName(proc))
                        {
                            try { p.Kill(true); } catch { }
                        }
                        using var key = Registry.LocalMachine.CreateSubKey(
                            $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{ProcessName}");
                        key?.SetValue("Debugger", @"C:\Windows\System32\systray.exe_nonexistent", RegistryValueKind.String);
                        if (log) Log("Refresh: network filter stopped, disabled, and re-quarantined.");
                    }
                    catch (Exception ex) { Log("Filter reinforce: " + ex.Message); }
                }

                if (IsExtensionsTweakEnabled())
                {
                    try
                    {
                        int n = 0;
                        n += DeleteRegistryTree(Registry.LocalMachine, @"SOFTWARE\Policies\Google", "HKLM");
                        n += DeleteRegistryTree(Registry.CurrentUser, @"Software\Policies\Google", "HKCU");
                        if (log && n > 0) Log("Refresh: Google policy keys cleared again.");
                    }
                    catch (Exception ex) { Log("Extensions reinforce: " + ex.Message); }
                }

                if (IsProgramsTweakEnabled())
                {
                    try
                    {
                        ReapplyProgramsUnblock();
                        if (log) Log("Refresh: program-blocking policies re-disabled.");
                    }
                    catch (Exception ex) { Log("Programs reinforce: " + ex.Message); }
                }
            }
            catch { }
        }

        private void RefreshAllStatuses(bool forceLog)
        {
            if (IsDisposed || lblFilterStatus == null || lblExtStatus == null || lblProgStatus == null) return;

            void Apply()
            {
                if (IsDisposed) return;

                // Explicit refresh (button / open): re-apply enabled tweaks
                if (forceLog)
                    ReinforceEnabledTweaksIfNeeded(log: true);

                // Background monitors only when THIS PROGRAM owns the enable
                bool filterOn = IsFilterTweakEnabled();
                bool extOn = IsExtensionsTweakEnabled();
                bool progOn = IsProgramsTweakEnabled();

                ApplyStatusLabel(lblFilterStatus, GetFilterUiStatus());
                ApplyStatusLabel(lblExtStatus, GetExtensionsUiStatus());
                ApplyStatusLabel(lblProgStatus, GetProgramsUiStatus());

                if (lblWarpStatus != null)
                {
                    if (!IsWarpInstalled())
                    {
                        lblWarpStatus.Text = "Not Installed";
                        lblWarpStatus.BackColor = Color.FromArgb(100, 100, 110);
                    }
                    else if (IsWarpConnected())
                    {
                        lblWarpStatus.Text = "Enabled (Connected)";
                        lblWarpStatus.BackColor = Success;
                    }
                    else
                    {
                        lblWarpStatus.Text = "Disabled (Disconnected)";
                        lblWarpStatus.BackColor = Danger;
                    }
                }

                // Gray out Install VPN when WARP is already present
                if (btnWarpInstall != null)
                    btnWarpInstall.Enabled = !IsWarpInstalled();

                if (!filterOn && chkMonitor != null && chkMonitor.Checked)
                {
                    chkMonitor.Checked = false;
                    if (forceLog) Log("Filter background stopped because tweak is not enabled in-program.");
                }
                if (chkMonitor != null)
                    chkMonitor.Enabled = filterOn;

                if (!extOn && chkExtMonitor != null && chkExtMonitor.Checked)
                {
                    chkExtMonitor.Checked = false;
                    if (forceLog) Log("Extensions background stopped because tweak is not enabled in-program.");
                }
                if (chkExtMonitor != null)
                    chkExtMonitor.Enabled = extOn;

                if (!progOn && chkProgMonitor != null && chkProgMonitor.Checked)
                {
                    chkProgMonitor.Checked = false;
                    if (forceLog) Log("Programs background stopped because tweak is not enabled in-program.");
                }
                if (chkProgMonitor != null)
                    chkProgMonitor.Enabled = progOn;

                if (forceLog)
                {
                    Log($"Status — Filter: {GetFilterUiStatus()}, Extensions: {GetExtensionsUiStatus()}, Programs: {GetProgramsUiStatus()}");
                }
            }

            if (InvokeRequired) BeginInvoke(new Action(Apply));
            else Apply();
        }

        private bool IsFilterTweakEnabled()
        {
            // Owned by this program
            try { return File.Exists(StateFilePath); }
            catch { return false; }
        }

        private bool IsExtensionsTweakEnabled()
        {
            // Enabled if we own the flag OR still have reversible backups from last session
            return File.Exists(ExtensionsFlagPath) || HasExtensionBackups();
        }

        private bool IsProgramsTweakEnabled()
        {
            return File.Exists(ProgramsStatePath);
        }

        /// <summary>Same practical effect as filter tweak without our state file.</summary>
        /// <summary>
        /// True when the network filter is fully suppressed like the tweak:
        /// service disabled or missing, process not running, process startup blocked or binary absent.
        /// </summary>
        private bool IsFilterFullySuppressed()
        {
            try
            {
                // 1) Process must not be running
                if (IsFilterProcessRunning())
                    return false;

                // 2) Service startup must be Disabled or service not installed
                if (FilterServiceExists())
                {
                    string start = GetServiceStartType(ServiceName);
                    bool serviceDisabled =
                        string.Equals(start, "Disabled", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(start, "4", StringComparison.OrdinalIgnoreCase);
                    if (!serviceDisabled)
                        return false;
                }
                // else: no service → OK for service side

                // 3) Process "startup" quarantined: binary missing, OR IFEO block present
                //    (same protections Enable Tweak applies)
                if (File.Exists(ProcessPath))
                {
                    if (!IsFilterProcessStartupBlocked())
                        return false;
                }
                // else: no program file → cannot start → OK

                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool IsFilterEffectPresentOutside()
        {
            if (IsFilterTweakEnabled()) return false;
            return IsFilterFullySuppressed();
        }

        private static bool FilterServiceExists()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
                return key != null;
            }
            catch { return false; }
        }

        private static bool IsFilterProcessRunning()
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(ProcessName);
                return Process.GetProcessesByName(name).Length > 0;
            }
            catch { return false; }
        }

        /// <summary>IFEO Debugger set for the filter exe (our quarantine method).</summary>
        private static bool IsFilterProcessStartupBlocked()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{ProcessName}");
                object? dbg = key?.GetValue("Debugger");
                return dbg != null && !string.IsNullOrWhiteSpace(dbg.ToString());
            }
            catch { return false; }
        }

        /// <summary>Google policy trees absent without our enable flag.</summary>
        private bool IsExtensionsEffectPresentOutside()
        {
            if (IsExtensionsTweakEnabled()) return false;
            try
            {
                using var hklm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Google");
                using var hkcu = Registry.CurrentUser.OpenSubKey(@"Software\Policies\Google");
                return hklm == null && hkcu == null;
            }
            catch { return false; }
        }

        /// <summary>Program-blocking policy values already non-blocking without our state.</summary>
        private bool IsProgramsEffectPresentOutside()
        {
            if (IsProgramsTweakEnabled()) return false;
            try
            {
                // If any known blocking switch is ON, effect is not "unblocked"
                foreach (var (hive, subKey, name, blocking) in new (RegistryKey, string, string, int)[]
                {
                    (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "DisallowRun", 1),
                    (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "DisallowRun", 1),
                    (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "DisallowRun", 1),
                    (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "RestrictRun", 1),
                    (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "RestrictRun", 1),
                })
                {
                    using var key = hive.OpenSubKey(subKey);
                    object? raw = key?.GetValue(name);
                    if (raw != null && Convert.ToInt32(raw) == blocking)
                        return false;
                }

                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Safer\CodeIdentifiers"))
                {
                    object? raw = key?.GetValue("DefaultLevel");
                    if (raw != null)
                    {
                        int v = Convert.ToInt32(raw);
                        if (v == 0 || v == 131072) // Disallowed / Basic User
                            return false;
                    }
                }

                foreach (string store in new[] { "Exe", "Dll", "Script", "Msi", "Appx" })
                {
                    using var key = Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\Policies\Microsoft\Windows\SrpV2" + store);
                    object? raw = key?.GetValue("EnforcementMode");
                    if (raw != null && Convert.ToInt32(raw) == 1)
                        return false;
                }

                // No blocking switches found active → same effect as our "unblocked" enable
                return true;
            }
            catch { return false; }
        }

        private enum TweakUiStatus { Disabled, EnabledByProgram, EnabledOutside }

        private TweakUiStatus GetFilterUiStatus()
        {
            if (IsFilterTweakEnabled()) return TweakUiStatus.EnabledByProgram;
            if (IsFilterEffectPresentOutside()) return TweakUiStatus.EnabledOutside;
            return TweakUiStatus.Disabled;
        }

        private TweakUiStatus GetExtensionsUiStatus()
        {
            if (IsExtensionsTweakEnabled()) return TweakUiStatus.EnabledByProgram;
            if (IsExtensionsEffectPresentOutside()) return TweakUiStatus.EnabledOutside;
            return TweakUiStatus.Disabled;
        }

        private TweakUiStatus GetProgramsUiStatus()
        {
            if (IsProgramsTweakEnabled()) return TweakUiStatus.EnabledByProgram;
            if (IsProgramsEffectPresentOutside()) return TweakUiStatus.EnabledOutside;
            return TweakUiStatus.Disabled;
        }

        private static void ApplyStatusLabel(Label lbl, TweakUiStatus status)
        {
            switch (status)
            {
                case TweakUiStatus.EnabledByProgram:
                    lbl.Text = "Enabled";
                    lbl.BackColor = Success;
                    break;
                case TweakUiStatus.EnabledOutside:
                    lbl.Text = "Enabled Outside Program";
                    lbl.BackColor = OutsideYellow;
                    break;
                default:
                    lbl.Text = "Disabled";
                    lbl.BackColor = Danger;
                    break;
            }
        }

        private string GetFilterStatusText()
        {
            return GetFilterUiStatus() switch
            {
                TweakUiStatus.EnabledByProgram => "Network Filter: Enabled",
                TweakUiStatus.EnabledOutside => "Network Filter: Enabled Outside Program",
                _ => "Network Filter: Disabled"
            };
        }

        private string GetExtensionsStatusText()
        {
            return GetExtensionsUiStatus() switch
            {
                TweakUiStatus.EnabledByProgram => "Extensions: Enabled",
                TweakUiStatus.EnabledOutside => "Extensions: Enabled Outside Program",
                _ => "Extensions: Disabled"
            };
        }

        private string GetProgramsStatusText()
        {
            return GetProgramsUiStatus() switch
            {
                TweakUiStatus.EnabledByProgram => "Programs: Enabled",
                TweakUiStatus.EnabledOutside => "Programs: Enabled Outside Program",
                _ => "Programs: Disabled"
            };
        }

        private string GetBackgroundStatusText()
        {
            var parts = new List<string>();
            if (chkMonitor?.Checked == true) parts.Add("Filter watch");
            if (chkExtMonitor?.Checked == true) parts.Add("Ext watch");
            if (chkProgMonitor?.Checked == true) parts.Add("Programs watch");
            if (parts.Count == 0) return "Background: Off";
            return "Background: " + string.Join(" + ", parts);
        }

        private bool AnyBackgroundEnabled()
        {
            return (chkMonitor?.Checked == true)
                || (chkExtMonitor?.Checked == true)
                || (chkProgMonitor?.Checked == true);
        }

        private void EnsureTrayIcon()
        {
            if (_trayIcon != null)
            {
                _trayIcon.Visible = true;
                UpdateTrayTooltip();
                return;
            }

            _trayIcon = new NotifyIcon
            {
                Text = "SocratesK12",
                Visible = true,
                Icon = SystemIcons.Shield
            };

            _trayIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    ShowMainWindow();
            };

            var menu = new ContextMenuStrip();
            menu.Opening += (s, e) =>
            {
                menu.Items.Clear();
                menu.Items.Add(new ToolStripMenuItem(GetFilterStatusText()) { Enabled = false });
                menu.Items.Add(new ToolStripMenuItem(GetExtensionsStatusText()) { Enabled = false });
                menu.Items.Add(new ToolStripMenuItem(GetProgramsStatusText()) { Enabled = false });
                menu.Items.Add(new ToolStripMenuItem(GetWarpStatusText()) { Enabled = false });
                menu.Items.Add(new ToolStripMenuItem(GetBackgroundStatusText()) { Enabled = false });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(new ToolStripMenuItem("Open window", null, (a, b) => ShowMainWindow()));
                menu.Items.Add(new ToolStripMenuItem("Disable background and close tray", null, (a, b) =>
                {
                    StopMonitoring();
                    ShowMainWindow();
                }));
            };
            _trayIcon.ContextMenuStrip = menu;
            UpdateTrayTooltip();
        }

        private void UpdateTrayTooltip()
        {
            if (_trayIcon == null) return;
            try
            {
                string tip = "SocratesK12 — " + GetBackgroundStatusText();
                if (tip.Length > 63) tip = tip.Substring(0, 63);
                _trayIcon.Text = tip;
            }
            catch { }
        }

        private void ShowMainWindow()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.BringToFront();
            this.Activate();
            RefreshAllStatuses(forceLog: true);
        }

        private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            if (AnyBackgroundEnabled() || _isMonitoring)
            {
                e.Cancel = true;
                this.Hide();
                Log("Window hidden. Monitor continues in tray.");
            }
            else
            {
                StopMonitoring();
                try { SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch; } catch { }
                _statusTimer?.Stop();
                _statusTimer?.Dispose();
                _trayIcon?.Dispose();
            }
        }

        // ===================== HELPERS =====================

        private static void RunSc(string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(15000);
        }

        private static List<StartupEntry> DisableStartupEntries(string processName, string processPath)
        {
            var removed = new List<StartupEntry>();
            string nameLower = processName.ToLowerInvariant();
            string pathLower = processPath.ToLowerInvariant();

            var targets = new (RegistryKey hive, string hiveName, string subKey)[]
            {
                (Registry.CurrentUser, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run"),
                (Registry.LocalMachine, "HKLM", @"Software\Microsoft\Windows\CurrentVersion\Run"),
                (Registry.LocalMachine, "HKLM", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
                (Registry.CurrentUser, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
                (Registry.LocalMachine, "HKLM", @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
            };

            foreach (var (hive, hiveName, subKey) in targets)
            {
                try
                {
                    using var key = hive.OpenSubKey(subKey, writable: true);
                    if (key == null) continue;
                    foreach (string valueName in key.GetValueNames().ToArray())
                    {
                        object? raw = key.GetValue(valueName);
                        string data = raw?.ToString() ?? "";
                        string dataLower = data.ToLowerInvariant();
                        if (dataLower.Contains(nameLower) || dataLower.Contains(pathLower))
                        {
                            removed.Add(new StartupEntry
                            {
                                Hive = hiveName,
                                KeyPath = subKey,
                                ValueName = valueName,
                                ValueData = data
                            });
                            key.DeleteValue(valueName, false);
                        }
                    }
                }
                catch { }
            }

            // Startup folder shortcuts
            try
            {
                string[] folders =
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
                };
                foreach (string folder in folders)
                {
                    if (!Directory.Exists(folder)) continue;
                    foreach (string file in Directory.GetFiles(folder, "*.lnk"))
                    {
                        // If shortcut name mentions the process, move aside (not delete permanently)
                        string fname = Path.GetFileName(file).ToLowerInvariant();
                        if (fname.Contains(nameLower.Replace(".exe", "")) || fname.Contains("aristotle"))
                        {
                            string bak = file + ".disabled_by_quarantine";
                            if (File.Exists(bak)) File.Delete(bak);
                            File.Move(file, bak);
                            removed.Add(new StartupEntry
                            {
                                Hive = "STARTUP_FOLDER",
                                KeyPath = folder,
                                ValueName = Path.GetFileName(file),
                                ValueData = bak
                            });
                        }
                    }
                }
            }
            catch { }

            return removed;
        }

        private static void RestoreStartupEntries(List<StartupEntry> entries)
        {
            foreach (var entry in entries)
            {
                try
                {
                    if (entry.Hive == "STARTUP_FOLDER")
                    {
                        string original = Path.Combine(entry.KeyPath, entry.ValueName);
                        if (File.Exists(entry.ValueData) && !File.Exists(original))
                            File.Move(entry.ValueData, original);
                        continue;
                    }

                    RegistryKey hive = entry.Hive == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
                    using var key = hive.CreateSubKey(entry.KeyPath);
                    key?.SetValue(entry.ValueName, entry.ValueData);
                }
                catch { }
            }
        }

        private static string GetServiceStartType(string serviceName)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{serviceName}");
                if (key != null && key.GetValue("Start") is int i)
                {
                    return i switch
                    {
                        2 => "Automatic",
                        3 => "Manual",
                        4 => "Disabled",
                        _ => i.ToString()
                    };
                }
            }
            catch { }
            return "Unknown";
        }

        private static string? GetServiceImagePath(string serviceName)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{serviceName}");
                return key?.GetValue("ImagePath")?.ToString();
            }
            catch { }
            return null;
        }
    }

    public class SavedPolicyValue
    {
        public string Hive { get; set; } = "";
        public string KeyPath { get; set; } = "";
        public string ValueName { get; set; } = "";
        public bool Existed { get; set; }
        public int ValueKind { get; set; }
        public int DwordValue { get; set; }
        public string? StringValue { get; set; }
    }

    public class StartupEntry
    {
        public string Hive { get; set; } = ""; // HKCU or HKLM
        public string KeyPath { get; set; } = "";
        public string ValueName { get; set; } = "";
        public string ValueData { get; set; } = "";
    }

    public class QuarantineState
    {
        public string ProcessPath { get; set; } = "";
        public string ProcessName { get; set; } = "";
        public string ServiceName { get; set; } = "";
        public string? OriginalImagePath { get; set; }
        public string OriginalStartType { get; set; } = "";
        public string? QuarantinedPath { get; set; }
        public bool IfeoApplied { get; set; }
        public DateTime QuarantinedAt { get; set; }
        public List<StartupEntry> RemovedStartupEntries { get; set; } = new();
    }
}
