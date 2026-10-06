using System.Diagnostics;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BO1Ranked;

// Companion app for the BO1 Zombies ranked mod (Plutonium T5).
//
// It links the game to the matchmaking server:
//   - the mod writes its state to   Plutonium\storage\t5\ranked\state.txt
//   - the app sends it to the server, which forwards it to the opponent
//   - the app writes the opponent's state to  ...\ranked\opponent.txt, which the mod displays
//   - before the game, the app writes the seed to  ...\ranked\match.txt, which the mod reads on map load
//
// It also installs / removes the mod files (see the "mod files" section below).
public class MainForm : Form
{
    private const string ServerUrl = "wss://ranked-d5oa.onrender.com";
    private const string ServerHttpUrl = "https://ranked-d5oa.onrender.com";     // ladder and profile pages

    // Theme.
    private static readonly Color ColorWindow = Color.FromArgb(31, 31, 31);
    private static readonly Color ColorSidebar = Color.FromArgb(22, 22, 22);
    private static readonly Color ColorCard = Color.FromArgb(42, 42, 42);
    private static readonly Color ColorHover = Color.FromArgb(58, 58, 58);
    private static readonly Color ColorAccent = Color.FromArgb(255, 85, 0);
    private static readonly Color ColorText = Color.FromArgb(237, 237, 237);
    private static readonly Color ColorMuted = Color.FromArgb(154, 154, 154);
    private static readonly Color ColorGood = Color.FromArgb(61, 220, 132);
    private static readonly Color ColorWarn = Color.FromArgb(255, 176, 32);
    private static readonly Color ColorBad = Color.FromArgb(255, 77, 77);

    // Public GitHub repository whose latest release holds the newest BO1Ranked.exe (see CheckForUpdate).
    private const string UpdateRepo = "BlancheLaGoat/BO1Ranked";
    private const string ExeName = "BO1Ranked.exe";

    private static readonly Version AppVersion = Assembly.GetExecutingAssembly().GetName().Version;

    // Full version text: "0.8.0" for a normal release, "0.8.0-beta.1" for a test version. It is the tag
    // the exe was built from. Beta builds play on a separate beta ladder and only meet other beta builds.
    private static readonly string AppVersionText = ReadVersionText();
    private static readonly bool IsBetaBuild = AppVersionText.Contains("-beta");

    private static string ReadVersionText()
    {
        var attribute = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        string text = attribute != null ? attribute.InformationalVersion : AppVersion.ToString(3);
        int plus = text.IndexOf('+');       // the build adds "+commit" after the version
        return plus >= 0 ? text.Substring(0, plus) : text;
    }
    private const string PluginUrl = "https://github.com/fedddddd/t5-gsc-utils/releases/latest/download/t5-gsc-utils.dll";
    private const string PluginFile = "t5-gsc-utils.dll";
    private const string BackupSuffix = ".before-bo1ranked";

    private const int TestGoal = 3;                 // round to reach in test mode
    private const int TestBotSecondsPerRound = 30;

    // Mod scripts embedded in the exe (see BO1Ranked.csproj) and copied to Plutonium\storage\t5\maps\.
    private static readonly string[] ModScripts =
    {
        "_zombiemode_weapons.gsc",
        "_zombiemode_powerups.gsc",
        "_zombiemode_ai_dogs.gsc",
        "ranked_rng.gsc",
        "ranked_link.gsc"
    };

    private static readonly string plutoniumDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Plutonium");

    private static readonly string mapsDir = Path.Combine(plutoniumDir, "storage", "t5", "maps");
    private static readonly string pluginsDir = Path.Combine(plutoniumDir, "plugins");
    private static readonly string storageDir = Path.Combine(plutoniumDir, "storage", "t5");
    private static readonly string modsDir = Path.Combine(storageDir, "mods");
    private static readonly string rawDir = Path.Combine(storageDir, "raw");
    private static readonly string rankedDir = Path.Combine(plutoniumDir, "storage", "t5", "ranked");

    private static readonly string settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BO1Ranked", "settings.txt");

    // Secret key created on first start. The server ties the player's name, Elo and history to it,
    // so nobody else can play under that name. Losing this file means starting a new account.
    private static readonly string identityPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BO1Ranked", "identity.txt");

    // Present only when this app downloaded the plugin itself: uninstall then removes it too.
    private static readonly string pluginMarkerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BO1Ranked", "plugin-installed-by-app");

    private readonly TextBox nameBox = new TextBox();
    private readonly Label modLabel = new Label();
    private readonly Button installButton = new Button();
    private readonly Button uninstallButton = new Button();
    private readonly Button searchButton = new Button();
    private readonly Button testButton = new Button();
    private readonly Button practiceButton = new Button();
    private readonly Button stopButton = new Button();
    private readonly Label statusLabel = new Label();
    private readonly Label matchLabel = new Label();
    private readonly Label opponentLabel = new Label();
    private readonly ListBox logBox = new ListBox();
    private readonly System.Windows.Forms.Timer pollTimer = new System.Windows.Forms.Timer();
    private readonly System.Windows.Forms.Timer cleanupTimer = new System.Windows.Forms.Timer();

    // Pages (one visible at a time) and their navigation buttons.
    private readonly Panel playPage = new Panel();
    private readonly Panel ladderPage = new Panel();
    private readonly Panel profilePage = new Panel();
    private readonly Panel modPage = new Panel();
    private readonly List<Button> navButtons = new List<Button>();
    private readonly List<Panel> pages = new List<Panel>();

    private readonly Label sideNameLabel = new Label();
    private readonly Label sideEloLabel = new Label();
    private readonly Label ladderStatusLabel = new Label();
    private readonly DataGridView ladderGrid = new DataGridView();
    private readonly Button saveNameButton = new Button();
    private readonly CheckBox betaBox = new CheckBox();
    private Label betaInfo = new Label();
    private bool betaChannel;
    private readonly Label profileEloLabel = new Label();
    private readonly Label profileRecordLabel = new Label();
    private readonly Label profileHintLabel = new Label();
    private readonly DataGridView historyGrid = new DataGridView();

    private static readonly HttpClient web = new HttpClient { Timeout = TimeSpan.FromSeconds(70) };
    private string identityToken = "";
    private string playerName = "";         // name last saved (the one sent to the server)

    private ClientWebSocket socket;
    private CancellationTokenSource socketCancel;
    private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);

    private bool modInstalled;
    private bool inMatch;
    private bool testMode;
    private int matchSeed;
    private string matchCode = "";           // identifies the match on the server
    private long recordingOffset;            // how much of ranked/replay.txt has already been sent to the server
    private bool recordingBusy;
    private bool violationSent;
    private FileSystemWatcher scriptWatcher;
    private FileSystemWatcher pluginWatcher;
    private int matchGoal;
    private string lastStateSent = "";
    private string pendingOpponentLine;      // line to write to opponent.txt (retried if the file is busy)
    private DateTime testStart;
    private bool testBotRunning;
    private bool testBotFinished;
    private bool readySent;                  // this player pressed "ready" in game and the server knows
    private bool goGiven;                    // go.txt written: the countdown is running or the race is on
    private bool practiceMatch;              // online practice against the server's bot: Elo is not touched
    private bool lostSent;                   // this player died or surrendered and the server knows
    private bool pauseSent;                  // the opponent has been told this game is paused
    private string lastRawState = "";
    private DateTime lastRawChange;
    private int lastOpponentRound;

    public MainForm()
    {
        Text = "BO1 Ranked v" + AppVersionText;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.75f);
        BackColor = ColorWindow;
        ForeColor = ColorText;
        ClientSize = new Size(840, 560);

        BuildSidebar();
        BuildPlayPage();
        BuildLadderPage();
        BuildProfilePage();
        BuildModPage();
        ShowPage(playPage);

        pollTimer.Interval = 500;
        pollTimer.Tick += async (s, e) => await PollTick();

        // The exchange folder only exists during a match. A few seconds after the end (time for the mod
        // to read the final result) it is removed, so nothing is left in the Plutonium folder.
        cleanupTimer.Interval = 3000;
        cleanupTimer.Tick += (s, e) =>
        {
            cleanupTimer.Stop();
            if (!inMatch) DeleteRankedFolder();
        };

        LoadSettings();
        LoadIdentity();
        DeleteRankedFolder();       // leftovers from a crash or from an older version
        FormClosing += (s, e) => { SaveSettings(); DeleteRankedFolder(); };

        if (!Directory.Exists(plutoniumDir))
        {
            Log("Plutonium folder not found: " + plutoniumDir);
        }
        RefreshModStatus();
        UpdateInstalledScripts();

        Shown += async (s, e) =>
        {
            await CheckForUpdate();
            await RefreshProfile();
        };
    }

    // Dark title bar on Windows 10/11. Purely cosmetic: ignored where it is not supported.
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int on = 1;
            DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));
        }
        catch
        {
        }
    }

    // ------------------------------------------------------------------ interface

    private const int SidebarWidth = 200;
    private const int PageWidth = 640;
    private const int PageMargin = 28;
    private const int ContentWidth = PageWidth - 2 * PageMargin;

    private static Label MakeLabel(string text, int x, int y, int width, int height, float size, FontStyle style, Color color)
    {
        return new Label
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(width, height),
            Font = new Font("Segoe UI", size, style),
            ForeColor = color,
            BackColor = Color.Transparent,
            AutoEllipsis = true
        };
    }

    private static void StyleButton(Button button, Color back, Color fore, float size, FontStyle style)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(back, 0.25f);
        button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(back, 0.1f);
        button.BackColor = back;
        button.ForeColor = fore;
        button.Font = new Font("Segoe UI", size, style);
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;
    }

    private static void StyleGrid(DataGridView grid)
    {
        grid.BackgroundColor = ColorWindow;
        grid.BorderStyle = BorderStyle.None;
        grid.GridColor = ColorHover;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = 34;
        grid.RowTemplate.Height = 30;
        grid.RowHeadersVisible = false;
        grid.ReadOnly = true;
        grid.MultiSelect = false;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.AllowUserToResizeColumns = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.ScrollBars = ScrollBars.Vertical;

        grid.ColumnHeadersDefaultCellStyle.BackColor = ColorSidebar;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = ColorMuted;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = ColorSidebar;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = ColorMuted;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9f, FontStyle.Bold);

        grid.DefaultCellStyle.BackColor = ColorCard;
        grid.DefaultCellStyle.ForeColor = ColorText;
        grid.DefaultCellStyle.SelectionBackColor = ColorHover;
        grid.DefaultCellStyle.SelectionForeColor = ColorText;
        grid.DefaultCellStyle.Font = new Font("Segoe UI", 9.75f);
    }

    private void AddColumn(DataGridView grid, string title, float weight)
    {
        int index = grid.Columns.Add(title, title);
        grid.Columns[index].FillWeight = weight;
        grid.Columns[index].SortMode = DataGridViewColumnSortMode.NotSortable;
    }

    private void AddPage(Panel page, string title)
    {
        page.Location = new Point(SidebarWidth, 0);
        page.Size = new Size(PageWidth, ClientSize.Height);
        page.BackColor = ColorWindow;
        page.Visible = false;
        page.Controls.Add(MakeLabel(title, PageMargin, 22, ContentWidth, 36, 18f, FontStyle.Bold, ColorText));
        pages.Add(page);
        Controls.Add(page);
    }

    private void ShowPage(Panel page)
    {
        for (int i = 0; i < pages.Count; i++)
        {
            bool active = pages[i] == page;
            pages[i].Visible = active;
            navButtons[i].BackColor = active ? ColorCard : ColorSidebar;
            navButtons[i].ForeColor = active ? ColorAccent : ColorText;
        }
    }

    private void BuildSidebar()
    {
        var sidebar = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(SidebarWidth, ClientSize.Height),
            BackColor = ColorSidebar
        };

        sidebar.Controls.Add(MakeLabel("BO1 RANKED", 18, 20, 180, 34, 14f, FontStyle.Bold, ColorAccent));
        sidebar.Controls.Add(IsBetaBuild
            ? MakeLabel("BETA BUILD", 20, 54, 178, 18, 8.5f, FontStyle.Bold, ColorWarn)
            : MakeLabel("Zombies 1v1", 20, 54, 178, 18, 8.5f, FontStyle.Regular, ColorMuted));

        string[] titles = { "PLAY", "LADDER", "PROFILE", "MOD FILES" };
        Panel[] targets = { playPage, ladderPage, profilePage, modPage };
        for (int i = 0; i < titles.Length; i++)
        {
            Panel target = targets[i];
            var button = new Button
            {
                Text = "    " + titles[i],
                Location = new Point(0, 96 + i * 46),
                Size = new Size(SidebarWidth, 46),
                TextAlign = ContentAlignment.MiddleLeft
            };
            StyleButton(button, ColorSidebar, ColorText, 10.5f, FontStyle.Bold);
            button.FlatAppearance.MouseOverBackColor = ColorCard;
            button.Click += async (s, e) =>
            {
                ShowPage(target);
                if (target == ladderPage) await RefreshLadder();
                if (target == profilePage) await RefreshProfile();
            };
            navButtons.Add(button);
            sidebar.Controls.Add(button);
        }

        // Player card at the bottom.
        sideNameLabel.Location = new Point(20, ClientSize.Height - 86);
        sideNameLabel.Size = new Size(170, 22);
        sideNameLabel.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        sideNameLabel.ForeColor = ColorText;
        sideNameLabel.AutoEllipsis = true;

        sideEloLabel.Location = new Point(20, ClientSize.Height - 62);
        sideEloLabel.Size = new Size(170, 20);
        sideEloLabel.Font = new Font("Segoe UI", 9f);
        sideEloLabel.ForeColor = ColorMuted;
        sideEloLabel.Text = "Elo -";

        sidebar.Controls.Add(sideNameLabel);
        sidebar.Controls.Add(sideEloLabel);
        sidebar.Controls.Add(MakeLabel("v" + AppVersionText, 20, ClientSize.Height - 34, 170, 18, 8f, FontStyle.Regular, ColorMuted));

        Controls.Add(sidebar);
    }

    private void BuildPlayPage()
    {
        AddPage(playPage, "Play");

        statusLabel.Location = new Point(PageMargin, 70);
        statusLabel.Size = new Size(ContentWidth, 26);
        statusLabel.Font = new Font("Segoe UI", 11.5f, FontStyle.Bold);
        statusLabel.ForeColor = ColorText;
        statusLabel.AutoEllipsis = true;
        statusLabel.Text = "Ready";

        searchButton.Text = "FIND MATCH";
        searchButton.Location = new Point(PageMargin, 106);
        searchButton.Size = new Size(ContentWidth, 60);
        StyleButton(searchButton, ColorAccent, Color.White, 14f, FontStyle.Bold);
        searchButton.Click += async (s, e) => await SearchClicked();

        // Match card.
        var card = new Panel
        {
            Location = new Point(PageMargin, 182),
            Size = new Size(ContentWidth, 92),
            BackColor = ColorCard
        };
        card.Controls.Add(MakeLabel("CURRENT MATCH", 16, 10, 300, 18, 8f, FontStyle.Bold, ColorMuted));

        matchLabel.Location = new Point(16, 32);
        matchLabel.Size = new Size(ContentWidth - 32, 24);
        matchLabel.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        matchLabel.ForeColor = ColorText;
        matchLabel.BackColor = Color.Transparent;
        matchLabel.AutoEllipsis = true;
        matchLabel.Text = "No match in progress";

        opponentLabel.Location = new Point(16, 60);
        opponentLabel.Size = new Size(ContentWidth - 32, 22);
        opponentLabel.ForeColor = ColorMuted;
        opponentLabel.BackColor = Color.Transparent;
        opponentLabel.AutoEllipsis = true;

        card.Controls.Add(matchLabel);
        card.Controls.Add(opponentLabel);

        // Two ways to try the mod alone, both against a bot and to round 3:
        //   online practice  the server plays the opponent, through the same path as a real match
        //   local test       nothing leaves this PC, works without the server
        int third = (ContentWidth - 20) / 3;

        practiceButton.Text = "Online practice (bot)";
        practiceButton.Location = new Point(PageMargin, 290);
        practiceButton.Size = new Size(third, 36);
        StyleButton(practiceButton, ColorCard, ColorText, 9.5f, FontStyle.Regular);
        practiceButton.Click += async (s, e) => await PracticeClicked();

        testButton.Text = "Local test (bot)";
        testButton.Location = new Point(PageMargin + third + 10, 290);
        testButton.Size = new Size(third, 36);
        StyleButton(testButton, ColorCard, ColorText, 9.5f, FontStyle.Regular);
        testButton.Click += (s, e) => StartTestMatch();

        stopButton.Text = "Leave match";
        stopButton.Location = new Point(PageMargin + 2 * (third + 10), 290);
        stopButton.Size = new Size(ContentWidth - 2 * (third + 10), 36);
        StyleButton(stopButton, ColorCard, ColorBad, 9.75f, FontStyle.Regular);
        stopButton.Enabled = false;
        stopButton.Click += async (s, e) => await StopClicked();

        logBox.Location = new Point(PageMargin, 342);
        logBox.Size = new Size(ContentWidth, 194);
        logBox.IntegralHeight = false;
        logBox.HorizontalScrollbar = true;
        logBox.BorderStyle = BorderStyle.None;
        logBox.BackColor = ColorSidebar;
        logBox.ForeColor = ColorMuted;
        logBox.Font = new Font("Consolas", 9f);

        playPage.Controls.AddRange(new Control[] { statusLabel, searchButton, card, practiceButton, testButton, stopButton, logBox });
    }

    private void BuildLadderPage()
    {
        AddPage(ladderPage, IsBetaBuild ? "Ladder (beta)" : "Ladder");

        var refresh = new Button { Text = "Refresh", Location = new Point(PageMargin + ContentWidth - 110, 24), Size = new Size(110, 32) };
        StyleButton(refresh, ColorCard, ColorText, 9.5f, FontStyle.Regular);
        refresh.Click += async (s, e) => await RefreshLadder();

        ladderStatusLabel.Location = new Point(PageMargin, 66);
        ladderStatusLabel.Size = new Size(ContentWidth, 20);
        ladderStatusLabel.ForeColor = ColorMuted;
        ladderStatusLabel.Text = "Players appear here after their first ranked match.";

        ladderGrid.Location = new Point(PageMargin, 94);
        ladderGrid.Size = new Size(ContentWidth, 442);
        StyleGrid(ladderGrid);
        AddColumn(ladderGrid, "#", 12);
        AddColumn(ladderGrid, "Player", 44);
        AddColumn(ladderGrid, "Elo", 16);
        AddColumn(ladderGrid, "W", 10);
        AddColumn(ladderGrid, "L", 10);
        AddColumn(ladderGrid, "Win %", 16);

        ladderPage.Controls.AddRange(new Control[] { refresh, ladderStatusLabel, ladderGrid });
        refresh.BringToFront();
    }

    private void BuildProfilePage()
    {
        AddPage(profilePage, "Profile");

        profilePage.Controls.Add(MakeLabel("PLAYER NAME", PageMargin, 72, 300, 18, 8f, FontStyle.Bold, ColorMuted));

        nameBox.Location = new Point(PageMargin, 94);
        nameBox.Size = new Size(ContentWidth - 122, 28);
        nameBox.MaxLength = 20;
        nameBox.BorderStyle = BorderStyle.FixedSingle;
        nameBox.BackColor = ColorCard;
        nameBox.ForeColor = ColorText;
        nameBox.Font = new Font("Segoe UI", 11f);

        saveNameButton.Text = "Save";
        saveNameButton.Location = new Point(PageMargin + ContentWidth - 110, 93);
        saveNameButton.Size = new Size(110, 30);
        StyleButton(saveNameButton, ColorAccent, Color.White, 9.5f, FontStyle.Bold);
        saveNameButton.Click += async (s, e) => await SaveNameClicked();

        profileHintLabel.Location = new Point(PageMargin, 128);
        profileHintLabel.Size = new Size(ContentWidth, 20);
        profileHintLabel.ForeColor = ColorMuted;
        profileHintLabel.Text = "3 to 20 characters: letters, digits, space, - and _";

        var card = new Panel
        {
            Location = new Point(PageMargin, 160),
            Size = new Size(ContentWidth, 96),
            BackColor = ColorCard
        };
        card.Controls.Add(MakeLabel("ELO", 16, 10, 100, 18, 8f, FontStyle.Bold, ColorMuted));

        profileEloLabel.Location = new Point(14, 28);
        profileEloLabel.Size = new Size(200, 56);
        profileEloLabel.Font = new Font("Segoe UI", 30f, FontStyle.Bold);
        profileEloLabel.ForeColor = ColorAccent;
        profileEloLabel.BackColor = Color.Transparent;
        profileEloLabel.Text = "-";

        profileRecordLabel.Location = new Point(230, 34);
        profileRecordLabel.Size = new Size(ContentWidth - 246, 48);
        profileRecordLabel.Font = new Font("Segoe UI", 10.5f);
        profileRecordLabel.ForeColor = ColorText;
        profileRecordLabel.BackColor = Color.Transparent;
        profileRecordLabel.Text = "No ranked match played yet";

        card.Controls.Add(profileEloLabel);
        card.Controls.Add(profileRecordLabel);

        profilePage.Controls.Add(MakeLabel("LAST MATCHES", PageMargin, 272, 300, 18, 8f, FontStyle.Bold, ColorMuted));

        historyGrid.Location = new Point(PageMargin, 296);
        historyGrid.Size = new Size(ContentWidth, 240);
        StyleGrid(historyGrid);
        AddColumn(historyGrid, "Result", 16);
        AddColumn(historyGrid, "Opponent", 34);
        AddColumn(historyGrid, "Elo", 12);
        AddColumn(historyGrid, "How", 22);
        AddColumn(historyGrid, "Date", 20);

        profilePage.Controls.AddRange(new Control[] { nameBox, saveNameButton, profileHintLabel, card, historyGrid });
    }

    private void BuildModPage()
    {
        AddPage(modPage, "Mod files");

        modLabel.Location = new Point(PageMargin, 72);
        modLabel.Size = new Size(ContentWidth, 26);
        modLabel.Font = new Font("Segoe UI", 11.5f, FontStyle.Bold);

        var info = MakeLabel(
            "The mod replaces the game's box, power-up and dog round scripts with seeded versions, " +
            "and adds a plugin that lets the game exchange files with this app.\n\n" +
            "It stays active in all your solo Zombies games, shows Plutonium's checksums and a " +
            "\"RANKED MOD ACTIVE\" banner, and must be uninstalled before any leaderboard run.",
            PageMargin, 108, ContentWidth, 170, 9.75f, FontStyle.Regular, ColorMuted);
        info.AutoEllipsis = false;

        installButton.Text = "Install mod files";
        installButton.Location = new Point(PageMargin, 292);
        installButton.Size = new Size(286, 40);
        StyleButton(installButton, ColorAccent, Color.White, 10f, FontStyle.Bold);
        installButton.Click += async (s, e) => await InstallClicked();

        uninstallButton.Text = "Uninstall mod files";
        uninstallButton.Location = new Point(PageMargin + 298, 292);
        uninstallButton.Size = new Size(ContentWidth - 298, 40);
        StyleButton(uninstallButton, ColorCard, ColorText, 10f, FontStyle.Regular);
        uninstallButton.Click += (s, e) => UninstallClicked();

        modPage.Controls.Add(MakeLabel("UPDATES", PageMargin, 362, 300, 18, 8f, FontStyle.Bold, ColorMuted));

        betaBox.Text = "Beta channel: receive test versions early";
        betaBox.Location = new Point(PageMargin, 384);
        betaBox.Size = new Size(ContentWidth, 26);
        betaBox.ForeColor = ColorText;
        betaBox.BackColor = ColorWindow;
        betaBox.FlatStyle = FlatStyle.Flat;
        betaBox.CheckedChanged += async (s, e) =>
        {
            if (betaChannel == betaBox.Checked) return;     // set by LoadSettings, not by the player
            betaChannel = betaBox.Checked;
            SaveSettings();
            await CheckForUpdate();
        };

        var checkButton = new Button { Text = "Check for updates", Location = new Point(PageMargin + ContentWidth - 170, 356), Size = new Size(170, 28) };
        StyleButton(checkButton, ColorCard, ColorText, 9f, FontStyle.Regular);
        checkButton.Click += async (s, e) =>
        {
            ShowPage(playPage);     // the result is written in the log of the Play page
            await CheckForUpdate();
        };
        modPage.Controls.Add(checkButton);

        betaInfo = MakeLabel(
            "Test versions have their own ladder and only meet other test versions, so nothing done in a " +
            "beta counts on the real ladder. Untick to go back to the normal version.",
            PageMargin, 414, ContentWidth, 110, 9.75f, FontStyle.Regular, ColorMuted);
        betaInfo.AutoEllipsis = false;

        modPage.Controls.AddRange(new Control[] { modLabel, info, installButton, uninstallButton, betaBox, betaInfo });
    }

    // ------------------------------------------------------------------ ladder and profile (read from the server's web pages)

    private async Task RefreshLadder()
    {
        ladderStatusLabel.Text = "Loading (the server can take up to a minute to wake up)...";
        try
        {
            string json = await web.GetStringAsync(ServerHttpUrl + "/ladder" + (IsBetaBuild ? "?beta=1" : ""));
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                ladderGrid.Rows.Clear();
                foreach (JsonElement row in document.RootElement.EnumerateArray())
                {
                    int wins = row.GetProperty("wins").GetInt32();
                    int losses = row.GetProperty("losses").GetInt32();
                    string name = row.GetProperty("name").GetString();
                    int index = ladderGrid.Rows.Add(
                        row.GetProperty("rank").GetInt32(), name, row.GetProperty("elo").GetInt32(),
                        wins, losses, WinRate(wins, losses));

                    if (string.Equals(name, playerName, StringComparison.OrdinalIgnoreCase))
                    {
                        ladderGrid.Rows[index].DefaultCellStyle.ForeColor = ColorAccent;
                        ladderGrid.Rows[index].DefaultCellStyle.SelectionForeColor = ColorAccent;
                    }
                }
                ladderGrid.ClearSelection();
                ladderStatusLabel.Text = ladderGrid.Rows.Count == 0
                    ? "No ranked match has been played yet."
                    : ladderGrid.Rows.Count + " ranked player(s)";
            }
        }
        catch (Exception)
        {
            ladderStatusLabel.Text = "Could not load the ladder - try Refresh in a minute.";
        }
    }

    private async Task RefreshProfile()
    {
        if (playerName == "") return;

        try
        {
            HttpResponseMessage response = await web.GetAsync(ServerHttpUrl + "/player?name=" + Uri.EscapeDataString(playerName) + (IsBetaBuild ? "&beta=1" : ""));
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Name not registered yet: the account is created on the first "Find match".
                // Every new account starts at 1000, so that is what is shown meanwhile.
                ShowStats(1000, 0, 0, 0);
                historyGrid.Rows.Clear();
                return;
            }
            response.EnsureSuccessStatusCode();

            using (JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
            {
                JsonElement root = document.RootElement;
                ShowStats(root.GetProperty("elo").GetInt32(), root.GetProperty("wins").GetInt32(),
                    root.GetProperty("losses").GetInt32(), root.GetProperty("rank").GetInt32());

                historyGrid.Rows.Clear();
                foreach (JsonElement match in root.GetProperty("matches").EnumerateArray())
                {
                    string winner = match.GetProperty("winner").GetString();
                    bool won = string.Equals(winner, root.GetProperty("name").GetString(), StringComparison.OrdinalIgnoreCase);
                    int delta = match.GetProperty("delta").GetInt32();
                    string reason = match.GetProperty("reason").GetString();
                    string how = reason == "finished" ? "Round 30 in " + FormatTime(match.GetProperty("timeMs").GetInt32().ToString())
                        : reason == "left" ? "Opponent left" : "Death / surrender";
                    if (!won && reason == "left") how = "Left the match";

                    string date = "";
                    DateTime played;
                    if (DateTime.TryParse(match.GetProperty("playedAt").GetString(), out played))
                    {
                        date = played.ToLocalTime().ToString("dd MMM HH:mm");
                    }

                    int index = historyGrid.Rows.Add(
                        won ? "WIN" : "LOSS",
                        won ? match.GetProperty("loser").GetString() : winner,
                        (won ? "+" : "-") + delta, how, date);
                    historyGrid.Rows[index].Cells[0].Style.ForeColor = won ? ColorGood : ColorBad;
                    historyGrid.Rows[index].Cells[0].Style.SelectionForeColor = won ? ColorGood : ColorBad;
                }
                historyGrid.ClearSelection();
            }
        }
        catch (Exception)
        {
            // Server asleep or offline: the profile simply keeps its last values.
        }
    }

    private void ShowStats(int elo, int wins, int losses, int rank)
    {
        profileEloLabel.Text = elo.ToString();
        sideEloLabel.Text = "Elo " + elo;
        profileRecordLabel.Text = wins + losses == 0
            ? "No ranked match played yet"
            : wins + " W  -  " + losses + " L   (" + WinRate(wins, losses) + ")\nRank #" + rank;
    }

    private static string WinRate(int wins, int losses)
    {
        return wins + losses == 0 ? "-" : (100 * wins / (wins + losses)) + " %";
    }

    private static string CleanName(string text)
    {
        string kept = Regex.Replace(text ?? "", "[^A-Za-z0-9 _-]", "");
        kept = Regex.Replace(kept, "\\s+", " ").Trim();
        return kept.Length > 20 ? kept.Substring(0, 20).Trim() : kept;
    }

    private async Task SaveNameClicked()
    {
        if (inMatch || socket != null)
        {
            profileHintLabel.ForeColor = ColorWarn;
            profileHintLabel.Text = "You cannot change your name during a search or a match.";
            return;
        }

        string name = CleanName(nameBox.Text);
        if (name.Length < 3)
        {
            profileHintLabel.ForeColor = ColorBad;
            profileHintLabel.Text = "Name too short. 3 to 20 characters: letters, digits, space, - and _";
            return;
        }

        nameBox.Text = name;
        playerName = name;
        sideNameLabel.Text = name;
        SaveSettings();
        profileHintLabel.ForeColor = ColorGood;
        profileHintLabel.Text = "Saved. The name is reserved on the server at your next match search.";
        await RefreshProfile();
    }

    // The secret key is created once and never shown. See identityPath.
    private void LoadIdentity()
    {
        try
        {
            if (File.Exists(identityPath)) identityToken = File.ReadAllText(identityPath).Trim();
            if (identityToken.Length < 32)
            {
                identityToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                Directory.CreateDirectory(Path.GetDirectoryName(identityPath));
                File.WriteAllText(identityPath, identityToken);
            }
        }
        catch (Exception ex)
        {
            Log("Could not read or create your player key: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ self-update

    // Looks at the latest GitHub release. If its tag (v1.2.3) is newer than this exe, downloads the new
    // exe, swaps it in place of the running one and restarts. A running exe cannot be overwritten on
    // Windows but it can be renamed, hence the ".old" file, deleted on the next start.
    private async Task CheckForUpdate()
    {
        string exePath = Environment.ProcessPath;
        if (exePath == null) return;

        // Started from source (Run-Dev.bat): never self-replace. Any other exe updates itself in place,
        // whatever its file name ("BO1Ranked (1).exe" after a second download, for example).
        string exeFile = Path.GetFileName(exePath);
        if (exePath.Contains(@"\bin\") || exeFile.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            Log("Update check skipped (running from source)");
            return;
        }

        try { File.Delete(exePath + ".old"); } catch { }

        try
        {
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(120);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("BO1Ranked");

                // Normal channel: the latest normal release. Beta channel: the newest release of all,
                // test versions (GitHub "pre-releases", tagged like v0.8.0-beta.1) included.
                string tag = null;
                if (betaChannel)
                {
                    string list = await http.GetStringAsync("https://api.github.com/repos/" + UpdateRepo + "/releases?per_page=20");
                    using (JsonDocument document = JsonDocument.Parse(list))
                    {
                        foreach (JsonElement release in document.RootElement.EnumerateArray())
                        {
                            if (release.GetProperty("draft").GetBoolean()) continue;
                            string candidate = release.GetProperty("tag_name").GetString();
                            if (!IsVersion(candidate)) continue;
                            if (!HasExe(release)) continue;     // still being built, or built without an exe
                            if (tag == null || CompareVersions(candidate, tag) > 0) tag = candidate;
                        }
                    }
                }
                else
                {
                    string one = await http.GetStringAsync("https://api.github.com/repos/" + UpdateRepo + "/releases/latest");
                    using (JsonDocument document = JsonDocument.Parse(one))
                    {
                        tag = document.RootElement.GetProperty("tag_name").GetString();
                        if (!HasExe(document.RootElement)) tag = null;
                    }
                    // A test version published by mistake as a normal release must never reach everyone.
                    if (tag != null && tag.Contains("-beta")) tag = null;
                }
                if (tag == null || !IsVersion(tag))
                {
                    Log("Update check: no published version found");
                    return;
                }

                // Newer version, or leaving the beta channel: back to the latest normal release.
                bool newer = CompareVersions(tag, AppVersionText) > 0;
                bool leavingBeta = !betaChannel && IsBetaBuild;
                if (!newer && !leavingBeta)
                {
                    Log("Up to date (" + (betaChannel ? "beta" : "normal") + " channel, newest is " + tag + ")");
                    return;
                }
                string latest = tag.TrimStart('v');
                if (inMatch || socket != null) return;      // never restart in the middle of a search or a match

                SetStatus("Updating to v" + latest + "...");
                Log("New version v" + latest + " found, downloading...");
                searchButton.Enabled = false;
                testButton.Enabled = false;
                installButton.Enabled = false;
                uninstallButton.Enabled = false;

                byte[] exe = await http.GetByteArrayAsync(
                    "https://github.com/" + UpdateRepo + "/releases/download/" + tag + "/" + ExeName);
                if (exe.Length < 1000000 || exe[0] != (byte)'M' || exe[1] != (byte)'Z')
                {
                    throw new InvalidOperationException("the downloaded file is not a valid exe");
                }

                File.WriteAllBytes(exePath + ".new", exe);
                File.Move(exePath, exePath + ".old", true);
                File.Move(exePath + ".new", exePath);

                Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
                Application.Exit();
            }
        }
        catch (HttpRequestException ex)
        {
            // 403 or 429 from GitHub = too many update checks from this connection in the last hour.
            bool limited = ex.StatusCode == System.Net.HttpStatusCode.Forbidden
                || ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests;
            Log(limited
                ? "Update check refused by GitHub (too many checks this hour) - try again later"
                : "Update check failed: " + ex.Message);
            SetStatus("Ready");
            SetIdleButtons();
        }
        catch (Exception ex)
        {
            Log("Update check failed: " + ex.Message);
            SetStatus("Ready");
            SetIdleButtons();
        }
    }

    // True when the release has its BO1Ranked.exe attached. The build takes a few minutes after a
    // release is published: until then the release exists but there is nothing to download.
    private static bool HasExe(JsonElement release)
    {
        JsonElement assets;
        if (!release.TryGetProperty("assets", out assets)) return false;
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() == ExeName) return true;
        }
        return false;
    }

    private static readonly Regex VersionPattern = new Regex("^v?([0-9]+)\\.([0-9]+)(\\.([0-9]+))?(-beta\\.?([0-9]*))?$");

    private static bool IsVersion(string text)
    {
        return text != null && VersionPattern.IsMatch(text);
    }

    // Orders two version texts. A normal release is newer than its own test versions:
    // 0.8.0-beta.1 < 0.8.0-beta.2 < 0.8.0 < 0.8.1-beta.1
    private static int CompareVersions(string a, string b)
    {
        Match x = VersionPattern.Match(a ?? "");
        Match y = VersionPattern.Match(b ?? "");
        if (!x.Success || !y.Success) return 0;

        for (int group = 1; group <= 4; group++)
        {
            if (group == 3) continue;       // group 3 is the ".patch" text, group 4 its number
            int left = ParseInt(x.Groups[group].Value);
            int right = ParseInt(y.Groups[group].Value);
            if (left != right) return left.CompareTo(right);
        }

        // Same numbers: no "-beta" beats "-beta", then the higher beta number wins.
        int betaLeft = x.Groups[5].Success ? ParseInt(x.Groups[6].Value) : int.MaxValue;
        int betaRight = y.Groups[5].Success ? ParseInt(y.Groups[6].Value) : int.MaxValue;
        return betaLeft.CompareTo(betaRight);
    }

    // ------------------------------------------------------------------ settings

    private void LoadSettings()
    {
        string betaChannelSaved = "";
        playerName = CleanName(Environment.UserName);
        try
        {
            if (File.Exists(settingsPath))
            {
                string[] lines = File.ReadAllLines(settingsPath);
                if (lines.Length > 0 && CleanName(lines[0]).Length >= 3) playerName = CleanName(lines[0]);
                if (lines.Length > 1) betaChannelSaved = lines[1].Trim();
            }
        }
        catch (Exception ex)
        {
            Log("Could not read settings: " + ex.Message);
        }

        if (playerName.Length < 3) playerName = "Player" + new Random().Next(1000, 10000);
        nameBox.Text = playerName;
        sideNameLabel.Text = playerName;

        // No choice saved yet: someone who downloaded a test version by hand stays on the beta channel,
        // otherwise the app would put the normal version back at its first start.
        betaChannel = betaChannelSaved == "" ? IsBetaBuild : betaChannelSaved == "beta=1";
        betaBox.Checked = betaChannel;

        // The beta option is for testers only: a normal build hides it, unless the app was started
        // with "-beta" or the beta channel is already on.
        bool showBeta = IsBetaBuild || betaChannel
            || Environment.GetCommandLineArgs().Any(a => a.TrimStart('-', '/').Equals("beta", StringComparison.OrdinalIgnoreCase));
        betaBox.Visible = showBeta;
        betaInfo.Visible = showBeta;
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
            File.WriteAllLines(settingsPath, new[] { playerName, betaChannel ? "beta=1" : "beta=0" });
        }
        catch
        {
            // Not important: the settings will just have to be typed again.
        }
    }

    // ------------------------------------------------------------------ mod files (install / uninstall)

    private static byte[] ReadEmbeddedScript(string fileName)
    {
        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("maps/" + fileName))
        {
            if (stream == null) throw new InvalidOperationException("Missing embedded file: " + fileName);
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                return memory.ToArray();
            }
        }
    }

    private static bool SameContent(string path, byte[] expected)
    {
        try
        {
            return File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(expected);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void RefreshModStatus()
    {
        int upToDate = 0;
        int present = 0;
        try
        {
            foreach (string script in ModScripts)
            {
                string path = Path.Combine(mapsDir, script);
                if (File.Exists(path)) present++;
                if (SameContent(path, ReadEmbeddedScript(script))) upToDate++;
            }
        }
        catch (Exception ex)
        {
            Log("Could not check the mod files: " + ex.Message);
        }

        bool plugin = File.Exists(Path.Combine(pluginsDir, PluginFile));
        modInstalled = plugin && upToDate == ModScripts.Length;

        if (modInstalled)
        {
            modLabel.Text = "Mod files: installed";
            modLabel.ForeColor = ColorGood;
            installButton.Text = "Reinstall mod files";
        }
        else if (present == 0 && plugin)
        {
            // The scripts are gone but the plugin is still loaded by the game: it changes the checksums.
            modLabel.Text = "Plugin still installed - click Uninstall to remove it";
            modLabel.ForeColor = ColorWarn;
            installButton.Text = "Install mod files";
        }
        else if (present > 0)
        {
            modLabel.Text = "Mod files: update needed";
            modLabel.ForeColor = ColorWarn;
            installButton.Text = "Update mod files";
        }
        else
        {
            modLabel.Text = "Mod files: not installed";
            modLabel.ForeColor = ColorBad;
            installButton.Text = "Install mod files";
        }
        uninstallButton.Enabled = present > 0 || plugin;
    }

    // After an app update the embedded scripts are newer than the installed ones. The player already
    // agreed to these files at install time, so they are refreshed without asking again.
    // Does nothing when the mod was never installed (or was uninstalled).
    private void UpdateInstalledScripts()
    {
        try
        {
            bool anyInstalled = false;
            foreach (string script in ModScripts)
            {
                string path = Path.Combine(mapsDir, script);
                if (File.Exists(path) && IsRankedScript(path)) anyInstalled = true;
            }
            if (!anyInstalled) return;

            foreach (string script in ModScripts)
            {
                string path = Path.Combine(mapsDir, script);
                byte[] content = ReadEmbeddedScript(script);
                if (SameContent(path, content)) continue;

                BackupIfNeeded(path);
                File.WriteAllBytes(path, content);
                Log("Mod file updated: " + script);
            }
        }
        catch (Exception ex)
        {
            Log("Could not update the mod files (is the game running?): " + ex.Message);
        }
        RefreshModStatus();
    }

    private async Task InstallClicked()
    {
        if (inMatch) return;

        if (!Directory.Exists(plutoniumDir))
        {
            MessageBox.Show(this,
                "Plutonium was not found in:\n" + plutoniumDir + "\n\nInstall Plutonium and run Black Ops once, then try again.",
                "BO1 Ranked", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var text = new StringBuilder();
        text.AppendLine("The following files will be added to your Plutonium folder:");
        text.AppendLine();
        text.AppendLine(mapsDir);
        foreach (string script in ModScripts) text.AppendLine("    " + script);
        text.AppendLine();
        text.AppendLine(pluginsDir);
        text.AppendLine("    " + PluginFile + "  (downloaded from its official GitHub page)");
        text.AppendLine();
        text.AppendLine("The three _zombiemode_*.gsc files replace the game's box, power-up and dog round scripts with seeded versions.");
        text.AppendLine("They stay active in ALL your solo Zombies games until you click \"Uninstall mod files\", so uninstall before any leaderboard run.");
        text.AppendLine();
        text.AppendLine("Files already there with the same name are backed up next to them (" + BackupSuffix + ").");
        text.AppendLine("Close the game before continuing.");
        text.AppendLine();
        text.AppendLine("Install now?");

        if (MessageBox.Show(this, text.ToString(), "BO1 Ranked - install mod files",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
        {
            return;
        }

        installButton.Enabled = false;
        uninstallButton.Enabled = false;
        try
        {
            // 1. Mod scripts, from the exe.
            Directory.CreateDirectory(mapsDir);
            foreach (string script in ModScripts)
            {
                string path = Path.Combine(mapsDir, script);
                byte[] content = ReadEmbeddedScript(script);
                if (SameContent(path, content))
                {
                    Log("Already up to date: " + script);
                    continue;
                }

                BackupIfNeeded(path);
                File.WriteAllBytes(path, content);
                Log("Installed: " + path);
            }

            // 2. Plugin, downloaded from its own release page.
            Directory.CreateDirectory(pluginsDir);
            string pluginPath = Path.Combine(pluginsDir, PluginFile);
            if (File.Exists(pluginPath))
            {
                Log("Already there: " + pluginPath);
            }
            else
            {
                SetStatus("Downloading " + PluginFile + "...");
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(60);
                    byte[] plugin = await http.GetByteArrayAsync(PluginUrl);
                    if (plugin.Length < 100000 || plugin[0] != (byte)'M' || plugin[1] != (byte)'Z')
                    {
                        throw new InvalidOperationException("The downloaded plugin does not look like a valid DLL.");
                    }
                    File.WriteAllBytes(pluginPath, plugin);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(pluginMarkerPath));
                File.WriteAllText(pluginMarkerPath, pluginPath);
                Log("Installed: " + pluginPath);
            }

            SetStatus("Mod files installed");
        }
        catch (HttpRequestException ex)
        {
            Log("Plugin download failed: " + ex.Message);
            SetStatus("Install failed: could not download the plugin");
        }
        catch (Exception ex)
        {
            Log("Install failed: " + ex.Message);
            SetStatus("Install failed (is the game still running?)");
        }

        installButton.Enabled = true;
        RefreshModStatus();
    }

    private void UninstallClicked()
    {
        if (inMatch) return;

        var text = new StringBuilder();
        text.AppendLine("The following files will be removed:");
        text.AppendLine();
        text.AppendLine(mapsDir);
        foreach (string script in ModScripts) text.AppendLine("    " + script);
        text.AppendLine();
        text.AppendLine(pluginsDir);
        text.AppendLine("    " + PluginFile);
        text.AppendLine();
        text.AppendLine("The plugin is removed too because the game loads it even without the scripts,");
        text.AppendLine("which changes your checksums. If another mod of yours needs it, answer No.");
        text.AppendLine();
        text.AppendLine("Files that were backed up during install are put back.");
        text.AppendLine("Close the game before continuing.");
        text.AppendLine();
        text.AppendLine("Uninstall now?");

        if (MessageBox.Show(this, text.ToString(), "BO1 Ranked - uninstall mod files",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            foreach (string script in ModScripts) RemoveAndRestore(Path.Combine(mapsDir, script));
            DeleteRankedFolder();
            RemoveFolderIfEmpty(mapsDir);
            RemoveAndRestore(Path.Combine(pluginsDir, PluginFile));
            File.Delete(pluginMarkerPath);
            SetStatus("Mod files removed - restart the game to unload the plugin");
        }
        catch (Exception ex)
        {
            Log("Uninstall failed: " + ex.Message);
            SetStatus("Uninstall failed - close the game and try again");
        }

        RefreshModStatus();
    }

    // Keeps a copy of a file we are about to replace. An existing backup is never overwritten,
    // so the backup is always the file that was there before the very first install.
    private void BackupIfNeeded(string path)
    {
        string backup = path + BackupSuffix;
        if (File.Exists(path) && !File.Exists(backup) && !IsRankedScript(path))
        {
            File.Copy(path, backup);
            Log("Backed up: " + backup);
        }
    }

    // True when the file is an older version of this mod: those are replaced, not backed up,
    // otherwise uninstalling would bring the old mod version back.
    private static bool IsRankedScript(string path)
    {
        string content = File.ReadAllText(path);
        return content.Contains("ranked_rng") || content.Contains("ranked_link");
    }

    private void RemoveAndRestore(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
            Log("Removed: " + path);
        }

        string backup = path + BackupSuffix;
        if (File.Exists(backup))
        {
            File.Move(backup, path);
            Log("Restored: " + path);
        }
    }

    // Leaves no empty folder behind when this app was the one that created it.
    private static void RemoveFolderIfEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch
        {
        }
    }

    private async Task<bool> RequireMod()
    {
        RefreshModStatus();
        if (!modInstalled)
        {
            ShowPage(modPage);
            MessageBox.Show(this,
                "The mod files are missing or out of date.\nClick \"" + installButton.Text + "\" first.",
                "BO1 Ranked", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        // Ranked matches are played with the mod's own scripts and plugin, plus the community tools
        // that the admins have allowed. A file is recognised by its content, not by its name.
        List<string> foreign = ForeignFiles();
        if (foreign.Count > 0)
        {
            var hashes = new Dictionary<string, string>();
            foreach (string path in foreign) hashes[path] = FileHash(path);

            try
            {
                string json = await web.GetStringAsync(ServerHttpUrl + "/allowed");
                var allowed = new HashSet<string>(JsonSerializer.Deserialize<string[]>(json) ?? new string[0]);
                foreign = foreign.Where(p => !allowed.Contains(hashes[p])).ToList();
            }
            catch (Exception ex)
            {
                Log("Could not get the list of allowed files: " + ex.Message);
            }
        }
        if (foreign.Count > 0)
        {
            // Tell the server, so that an admin can allow a tool that everyone uses.
            try
            {
                var report = foreign.Take(30).Select(p => new Dictionary<string, string>
                {
                    ["hash"] = FileHash(p),
                    ["name"] = ShortPath(p)
                }).ToList();
                var request = new HttpRequestMessage(HttpMethod.Post, ServerHttpUrl + "/seen");
                request.Headers.Add("X-Player-Token", identityToken);
                request.Content = new StringContent(JsonSerializer.Serialize(report), Encoding.UTF8, "application/json");
                await web.SendAsync(request);
            }
            catch (Exception)
            {
                // reporting is only a convenience
            }

            var text = new StringBuilder();
            text.AppendLine("These scripts or plugins are installed in Plutonium and are not on the list of files");
            text.AppendLine("allowed in ranked. Move them out of the Plutonium folder, or ask an admin to allow them");
            text.AppendLine("(they have just been reported), then try again.");
            text.AppendLine();
            foreach (string path in foreign.Take(12)) text.AppendLine(ShortPath(path));
            if (foreign.Count > 12) text.AppendLine("... and " + (foreign.Count - 12) + " more");
            foreach (string path in foreign) Log("Not allowed in ranked: " + ShortPath(path));
            MessageBox.Show(this, text.ToString(), "BO1 Ranked", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ buttons

    private async Task SearchClicked()
    {
        if (inMatch) return;

        // Second click while searching = cancel.
        if (socket != null)
        {
            await SendLine("CANCEL");
            await Disconnect();
            SetStatus("Search cancelled");
            SetIdleButtons();
            return;
        }

        if (!await RequireMod()) return;

        practiceMatch = false;
        searchButton.Text = "CANCEL SEARCH";
        await ConnectAndSend("QUEUE");
    }

    private async Task PracticeClicked()
    {
        if (inMatch || socket != null) return;
        if (!await RequireMod()) return;

        practiceMatch = true;
        searchButton.Enabled = false;
        await ConnectAndSend("PRACTICE");
    }

    // Opens the connection, identifies the player, then sends the first request:
    // QUEUE for a ranked search, PRACTICE for a match against the server's bot.
    private async Task ConnectAndSend(string request)
    {
        SaveSettings();
        testButton.Enabled = false;
        practiceButton.Enabled = false;
        installButton.Enabled = false;
        uninstallButton.Enabled = false;
        SetStatus("Connecting to the server (can take up to a minute)...");

        try
        {
            socketCancel = new CancellationTokenSource();
            socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(ServerUrl), socketCancel.Token);
        }
        catch (Exception)
        {
            Log("Server unreachable");
            SetStatus("Server unreachable - try again in a minute");
            await Disconnect();
            SetIdleButtons();
            return;
        }

        _ = ReceiveLoop(socket, socketCancel.Token);

        await SendLine("HELLO;" + playerName + ";" + AppVersionText + ";" + identityToken);
        await SendLine(request);
    }

    private async void StartTestMatch()
    {
        if (inMatch || socket != null) return;
        if (!await RequireMod()) return;

        practiceMatch = false;
        testMode = true;
        testBotRunning = false;
        testBotFinished = false;
        BeginMatch(new Random().Next(1, 1000000), TestGoal, "Test bot", 0);
    }

    private async Task StopClicked()
    {
        if (!inMatch) return;

        if (!testMode) await SendLine("LEAVE");
        EndMatch("Match left");
        await Disconnect();
    }

    private void SetIdleButtons()
    {
        searchButton.Enabled = true;
        searchButton.Text = "FIND MATCH";
        testButton.Enabled = true;
        practiceButton.Enabled = true;
        stopButton.Enabled = false;
        installButton.Enabled = true;
        RefreshModStatus();
    }

    // ------------------------------------------------------------------ match flow

    private void BeginMatch(int seed, int goal, string opponentName, int opponentElo)
    {
        inMatch = true;
        matchSeed = seed;
        matchGoal = goal;
        lastStateSent = "";
        pendingOpponentLine = null;
        readySent = false;
        goGiven = false;
        lostSent = false;
        pauseSent = false;
        lastRawState = "";
        lastRawChange = DateTime.UtcNow;
        lastOpponentRound = 0;

        recordingOffset = 0;
        if (!testMode)
        {
            StartFileWatch();
            _ = SendLine("INTEGRITY;" + InstalledFilesHash() + ";" + PlutoniumFilesHash());
        }

        cleanupTimer.Stop();
        try
        {
            Directory.CreateDirectory(rankedDir);
            DeleteRankedFile("opponent.txt");
            DeleteRankedFile("go.txt");
            File.WriteAllText(Path.Combine(rankedDir, "match.txt"), seed + ";" + goal);
        }
        catch (Exception ex)
        {
            Log("Could not write match.txt: " + ex.Message);
        }

        searchButton.Enabled = false;
        searchButton.Text = practiceMatch ? "PRACTICE IN PROGRESS" : "MATCH IN PROGRESS";
        testButton.Enabled = false;
        practiceButton.Enabled = false;
        installButton.Enabled = false;
        uninstallButton.Enabled = false;
        stopButton.Enabled = true;

        SetStatus("Match found: start Kino (solo), then ready up in game");
        matchLabel.Text = "vs " + opponentName + (opponentElo > 0 ? "  (" + opponentElo + " Elo)" : "")
            + "     seed " + seed + "     goal: round " + goal;
        opponentLabel.Text = "Waiting for both players to ready up in game";
        ShowPage(playPage);
        Log("Match vs " + opponentName + ", seed " + seed);

        pollTimer.Start();
    }

    private void EndMatch(string status)
    {
        matchCode = "";
        StopFileWatch();
        inMatch = false;
        testMode = false;
        pollTimer.Stop();
        DeleteRankedFile("match.txt");      // also frees a player still waiting at spawn
        DeleteRankedFile("go.txt");
        cleanupTimer.Stop();
        cleanupTimer.Start();               // removes the whole folder in a few seconds

        SetIdleButtons();
        SetStatus(status);
        Log(status);
        matchLabel.Text = "No match in progress";
        opponentLabel.Text = "Last result: " + status;
    }

    // ------------------------------------------------------------------ anti-cheat
    //
    // 1. Live recording. The mod appends one line per second to ranked/replay.txt. Every new line is
    //    forwarded to the server while the match is running, so the server builds the recording itself
    //    and stamps each line with its own clock: it cannot be rewritten after the match.
    // 2. File check. A ranked match needs exactly the mod's scripts and plugin, unmodified, and nothing
    //    else that the game could load. Checked before a search, and watched during the whole match.

    private async Task StreamRecording()
    {
        if (recordingBusy) return;
        recordingBusy = true;
        try
        {
            string path = Path.Combine(rankedDir, "replay.txt");
            if (!File.Exists(path)) return;

            string text;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length < recordingOffset) recordingOffset = 0;      // the mod started a new file
                if (stream.Length == recordingOffset) return;
                stream.Seek(recordingOffset, SeekOrigin.Begin);
                var buffer = new byte[Math.Min(stream.Length - recordingOffset, 64 * 1024)];
                int read = stream.Read(buffer, 0, buffer.Length);
                text = Encoding.UTF8.GetString(buffer, 0, read);
            }

            // Only complete lines: the mod may be in the middle of writing the last one.
            int end = text.LastIndexOf('\n');
            if (end < 0) return;
            recordingOffset += Encoding.UTF8.GetByteCount(text.Substring(0, end + 1));

            foreach (string line in text.Substring(0, end).Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0) await SendLine("REC;" + trimmed);
            }
        }
        catch (IOException)
        {
            // the game is writing the file: the rest goes out on the next tick
        }
        finally
        {
            recordingBusy = false;
        }
    }

    // Short fingerprint of the installed mod scripts. Two players on the same version have the same one.
    private static string InstalledFilesHash()
    {
        try
        {
            using (var sha = SHA256.Create())
            using (var all = new MemoryStream())
            {
                foreach (string script in ModScripts)
                {
                    byte[] bytes = File.ReadAllBytes(Path.Combine(mapsDir, script));
                    all.Write(bytes, 0, bytes.Length);
                }
                return Convert.ToHexString(sha.ComputeHash(all.ToArray())).Substring(0, 16).ToLowerInvariant();
            }
        }
        catch (Exception)
        {
            return "unreadable";
        }
    }

    // Fingerprint of the scripts shipped by Plutonium (storage\t5\raw): names and contents. Two players
    // with the same Plutonium version have the same one, so a script added or edited there shows up
    // as a difference between the two players.
    private static string PlutoniumFilesHash()
    {
        try
        {
            if (!Directory.Exists(rawDir)) return "none";
            using (var sha = SHA256.Create())
            using (var all = new MemoryStream())
            {
                var paths = Directory.EnumerateFiles(rawDir, "*", SearchOption.AllDirectories)
                    .Where(p => { string e = Path.GetExtension(p).ToLowerInvariant(); return e == ".gsc" || e == ".csc"; })
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
                foreach (string path in paths)
                {
                    byte[] name = Encoding.UTF8.GetBytes(path.Substring(rawDir.Length).ToLowerInvariant());
                    byte[] bytes = File.ReadAllBytes(path);
                    all.Write(name, 0, name.Length);
                    all.Write(bytes, 0, bytes.Length);
                }
                return Convert.ToHexString(sha.ComputeHash(all.ToArray())).Substring(0, 16).ToLowerInvariant();
            }
        }
        catch (Exception)
        {
            return "unreadable";
        }
    }

    private static string FileHash(string path)
    {
        try
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
            }
        }
        catch (Exception)
        {
            return "unreadable";
        }
    }

    // Path shown to the player and to the admins: relative to the Plutonium folder, without the user name.
    private static string ShortPath(string path)
    {
        return path.StartsWith(plutoniumDir, StringComparison.OrdinalIgnoreCase)
            ? path.Substring(plutoniumDir.Length).TrimStart('\\', '/') : Path.GetFileName(path);
    }

    // Scripts the game can load in a normal solo game. The "mods" folder is left alone: a mod
    // (Strat Tester, for example) only runs when the player loads it, and the ranked mod itself
    // refuses to play a match while a mod is loaded.
    private static bool IsScriptFile(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension != ".gsc" && extension != ".csc") return false;
        // "raw" belongs to Plutonium, which updates it: it is not blocked, but its content is compared
        // between the two players of a match (see PlutoniumFilesHash).
        return !path.StartsWith(modsDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith(rawDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // Scripts and plugins that are not part of the mod: the game could load them during a ranked match.
    private static List<string> ForeignFiles()
    {
        var found = new List<string>();
        try
        {
            if (Directory.Exists(storageDir))
            {
                foreach (string path in Directory.EnumerateFiles(storageDir, "*", SearchOption.AllDirectories))
                {
                    if (!IsScriptFile(path)) continue;
                    bool ours = string.Equals(Path.GetDirectoryName(path), mapsDir, StringComparison.OrdinalIgnoreCase)
                        && ModScripts.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
                    if (!ours) found.Add(path);
                }
            }
            if (Directory.Exists(pluginsDir))
            {
                foreach (string path in Directory.EnumerateFiles(pluginsDir, "*.dll"))
                {
                    if (!string.Equals(Path.GetFileName(path), PluginFile, StringComparison.OrdinalIgnoreCase)) found.Add(path);
                }
            }
        }
        catch (Exception)
        {
            // a folder that cannot be read is not a reason to block the player
        }
        return found;
    }

    // During a match, any script or plugin file that is created, changed, renamed or removed ends the
    // match as a defeat - even if it is put back a moment later.
    private void StartFileWatch()
    {
        StopFileWatch();
        violationSent = false;
        try
        {
            if (Directory.Exists(storageDir))
            {
                scriptWatcher = new FileSystemWatcher(storageDir) { IncludeSubdirectories = true };
                HookWatcher(scriptWatcher, path => IsScriptFile(path));
            }
            if (Directory.Exists(pluginsDir))
            {
                pluginWatcher = new FileSystemWatcher(pluginsDir);
                HookWatcher(pluginWatcher, path => Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            Log("Could not watch the mod files: " + ex.Message);
        }
    }

    private void HookWatcher(FileSystemWatcher watcher, Func<string, bool> relevant)
    {
        watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime;
        FileSystemEventHandler changed = (s, e) => { if (relevant(e.FullPath)) ReportViolation(e.FullPath); };
        watcher.Changed += changed;
        watcher.Created += changed;
        watcher.Deleted += changed;
        watcher.Renamed += (s, e) => { if (relevant(e.FullPath) || relevant(e.OldFullPath)) ReportViolation(e.FullPath); };
        watcher.EnableRaisingEvents = true;
    }

    private void StopFileWatch()
    {
        try { scriptWatcher?.Dispose(); } catch { }
        try { pluginWatcher?.Dispose(); } catch { }
        scriptWatcher = null;
        pluginWatcher = null;
    }

    // Called from a background thread by the file watchers.
    private void ReportViolation(string path)
    {
        try
        {
            BeginInvoke(new Action(async () =>
            {
                if (!inMatch || testMode || violationSent) return;
                violationSent = true;

                string shortPath = ShortPath(path);
                Log("File changed during the match: " + shortPath);
                await SendLine("VIOLATION;" + shortPath.Replace(';', '_'));

                // After the start, the server answers with the defeat. Before it, the match is just cancelled.
                if (!goGiven) EndMatch("Match cancelled: a script or plugin file was changed");
                else SetStatus("DEFEAT: a script or plugin file was changed during the match");
            }));
        }
        catch (Exception)
        {
            // the window is closing
        }
    }

    private async Task PollTick()
    {
        if (!inMatch) return;

        FlushOpponentFile();

        // Recording first: the line for the final round must reach the server before the final state.
        if (!testMode && goGiven) await StreamRecording();
        if (!inMatch) return;

        // --- my state: state.txt -> server ---
        // Format: round;zone;down;time_ms;finished;finish_time_ms;seed;goal;ready
        string line = ReadRankedFile("state.txt");
        string[] f = line == null ? new string[0] : line.Trim().Split(';');

        // Ignore the file until it comes from this match's game (different seed = older game).
        bool mine = f.Length >= 9 && f[6] == matchSeed.ToString();

        // Synchronised start: the player is held at spawn until both sides are ready.
        if (mine && !readySent && f[8] == "1")
        {
            readySent = true;
            if (testMode)
            {
                GiveGo();       // the bot is always ready
            }
            else
            {
                SetStatus("Ready - waiting for your opponent");
                await SendLine("READY");
            }
        }

        if (mine && goGiven)
        {
            // Died or surrendered in game (finished field = 2): immediate defeat.
            if (f[4] == "2")
            {
                if (testMode)
                {
                    EndMatch("DEFEAT: you died or surrendered");
                }
                else if (!lostSent)
                {
                    lostSent = true;
                    await SendLine("LOST");
                }
                return;
            }

            // The mod rewrites state.txt twice a second. If it stops changing, the game is paused
            // (or frozen): the race goes on, and the opponent is told.
            if (line != lastRawState)
            {
                lastRawState = line;
                lastRawChange = DateTime.UtcNow;
                pauseSent = false;
            }
            else if (!testMode && !pauseSent && (DateTime.UtcNow - lastRawChange).TotalSeconds > 3)
            {
                pauseSent = true;
                lastStateSent = "";
                await SendLine("STATE;" + f[0] + ";paused;" + f[2] + ";" + f[3] + ";0;0");
            }

            string state = string.Join(";", f, 0, 6);
            if (state != lastStateSent)
            {
                lastStateSent = state;
                if (f[0] != "0") SetStatus("Racing: round " + f[0]);

                if (testMode)
                {
                    if (f[4] == "1" && !testBotFinished)
                    {
                        EndMatch("VICTORY in " + FormatTime(f[5]));
                        return;
                    }
                }
                else
                {
                    await SendLine("STATE;" + state);
                }
            }
        }

        if (testMode && testBotRunning) TestBotTick();
    }

    // Tells the mod to start: it shows a 3 second countdown, then releases the player and the zombies.
    private void GiveGo()
    {
        if (goGiven) return;

        try
        {
            File.WriteAllText(Path.Combine(rankedDir, "go.txt"), matchSeed.ToString());
        }
        catch (Exception ex)
        {
            Log("Could not write go.txt: " + ex.Message);
            return;
        }

        goGiven = true;
        SetStatus("Both players ready - GO!");
        Log("Both players ready, race started");

        if (testMode)
        {
            // The bot starts with the player, after the in-game countdown.
            testBotRunning = true;
            testStart = DateTime.UtcNow.AddSeconds(3);
        }
    }

    // Test mode opponent: gains one round every TestBotSecondsPerRound seconds.
    private void TestBotTick()
    {
        string[] zones = { "foyer_zone", "vip_zone", "dining_zone", "dressing_zone", "stage_zone", "theater_zone" };

        int seconds = (int)(DateTime.UtcNow - testStart).TotalSeconds;
        if (seconds < 0) return;        // countdown still running
        int round = Math.Min(matchGoal, 1 + seconds / TestBotSecondsPerRound);
        string zone = zones[(seconds / 10) % zones.Length];
        int down = (seconds % 40) >= 35 ? 1 : 0;
        int finished = round >= matchGoal ? 1 : 0;

        ShowOpponent(round, zone, down, finished);

        if (finished == 1 && !testBotFinished)
        {
            testBotFinished = true;
            FlushOpponentFile();
            EndMatch("DEFEAT: the bot reached round " + matchGoal);
        }
    }

    private void ShowOpponent(int round, string zone, int down, int finished)
    {
        lastOpponentRound = round;
        pendingOpponentLine = round + ";" + zone + ";" + down + ";" + finished;
        opponentLabel.Text = "Opponent: round " + round + " - " + zone + (down == 1 ? " - DOWN" : "");
    }

    // ------------------------------------------------------------------ files shared with the game

    private string ReadRankedFile(string name)
    {
        try
        {
            string path = Path.Combine(rankedDir, name);
            if (!File.Exists(path)) return null;

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }
        catch (IOException)
        {
            return null;    // the game is writing it: try again on the next tick
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Writes opponent.txt through a temporary file so the game never reads a half-written file.
    private void FlushOpponentFile()
    {
        if (pendingOpponentLine == null) return;

        try
        {
            string temp = Path.Combine(rankedDir, "opponent.tmp");
            File.WriteAllText(temp, pendingOpponentLine);
            File.Move(temp, Path.Combine(rankedDir, "opponent.txt"), true);
            pendingOpponentLine = null;
        }
        catch (IOException)
        {
            // The game is reading the file: try again on the next tick.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void DeleteRankedFolder()
    {
        try
        {
            if (Directory.Exists(rankedDir)) Directory.Delete(rankedDir, true);
        }
        catch
        {
            // A file is still in use: it will be removed at the next match end or app start.
        }
    }

    private void DeleteRankedFile(string name)
    {
        try
        {
            File.Delete(Path.Combine(rankedDir, name));
        }
        catch
        {
            // Missing or busy file: harmless.
        }
    }

    // ------------------------------------------------------------------ network

    private async Task SendLine(string line)
    {
        ClientWebSocket ws = socket;
        if (ws == null || ws.State != WebSocketState.Open) return;

        await sendLock.WaitAsync();
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log("Send failed: " + ex.Message);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken cancel)
    {
        var buffer = new byte[2048];
        try
        {
            while (ws.State == WebSocketState.Open && !cancel.IsCancellationRequested)
            {
                var text = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancel);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close) break;

                string line = text.ToString();
                BeginInvoke(new Action(() => HandleServerLine(line)));
            }
        }
        catch (OperationCanceledException)
        {
            return;     // intended disconnect
        }
        catch (Exception)
        {
        }

        if (!cancel.IsCancellationRequested && !IsDisposed)
        {
            BeginInvoke(new Action(() => ConnectionLost(ws)));
        }
    }

    private async void ConnectionLost(ClientWebSocket ws)
    {
        if (socket != ws) return;

        await Disconnect();
        if (inMatch)
        {
            EndMatch("Connection to the server lost");
        }
        else
        {
            SetStatus("Connection to the server lost");
            SetIdleButtons();
        }
    }

    private async Task Disconnect()
    {
        ClientWebSocket ws = socket;
        CancellationTokenSource cancel = socketCancel;
        socket = null;
        socketCancel = null;

        if (ws == null) return;

        try
        {
            if (ws.State == WebSocketState.Open)
            {
                using (var timeout = new CancellationTokenSource(2000))
                {
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
                }
            }
        }
        catch
        {
            // The connection was already dead.
        }

        if (cancel != null) cancel.Cancel();
        ws.Dispose();
    }

    private async void HandleServerLine(string line)
    {
        string[] f = line.Split(';');
        switch (f[0])
        {
            case "WELCOME":
                Log("Connected to the server (" + (f.Length > 1 ? f[1] : "?") + " player(s) online)");
                if (f.Length >= 6) ShowStats(ParseInt(f[2]), ParseInt(f[3]), ParseInt(f[4]), ParseInt(f[5]));
                break;

            case "BANNED":
                await Disconnect();
                SetIdleButtons();
                SetStatus("This account is banned from ranked play");
                Log("Search stopped: this account has been banned by an administrator");
                break;

            case "NAMETAKEN":
            case "NAMEINVALID":
                await Disconnect();
                SetIdleButtons();
                SetStatus(f[0] == "NAMETAKEN"
                    ? "The name \"" + playerName + "\" is already taken - pick another one"
                    : "Invalid name - pick another one");
                Log("Search stopped: change your name in the Profile page");
                ShowPage(profilePage);
                break;

            case "WAITING":
                // The server first looks for an opponent close to your Elo, then widens the range as time passes.
                SetStatus(f.Length >= 2
                    ? "Looking for an opponent (within " + f[1] + " Elo)..."
                    : "Looking for an opponent...");
                break;

            case "MATCH":
                if (f.Length >= 4)
                {
                    testMode = false;
                    matchCode = f.Length >= 6 ? f[5] : "";
                    BeginMatch(ParseInt(f[1]), ParseInt(f[2]), f[3], f.Length >= 5 ? ParseInt(f[4]) : 0);
                }
                break;

            case "GO":
                if (inMatch) GiveGo();
                break;

            case "OPP":
                if (inMatch && f.Length >= 6)
                {
                    ShowOpponent(ParseInt(f[1]), f[2], ParseInt(f[3]), ParseInt(f[5]));
                }
                break;

            case "RESULT":
                if (inMatch && f.Length >= 4)
                {
                    // Elo change, when the server sent it.
                    string elo = "";
                    if (practiceMatch)
                    {
                        elo = "   (practice, Elo unchanged)";
                    }
                    else if (f.Length >= 6)
                    {
                        int delta = ParseSigned(f[5]);
                        elo = "   (" + (delta >= 0 ? "+" : "") + delta + " Elo, now " + f[4] + ")";
                    }

                    // A time of 0 means the match ended by a death or a surrender, not by reaching the goal.
                    if (f[1] == "WIN" && f[2] == "0")
                    {
                        ShowOpponent(lastOpponentRound, "none", 0, 2);
                        FlushOpponentFile();
                        EndMatch("VICTORY: your opponent is out" + elo);
                    }
                    else if (f[1] == "WIN")
                    {
                        EndMatch("VICTORY in " + FormatTime(f[2]) + elo);
                    }
                    else if (f[3] == "0")
                    {
                        EndMatch("DEFEAT: you are out" + elo);
                    }
                    else
                    {
                        FlushOpponentFile();
                        EndMatch("DEFEAT: opponent finished in " + FormatTime(f[3]) + elo);
                    }
                    await Disconnect();
                    await RefreshProfile();
                }
                break;

            case "OPPLEFT":
                if (inMatch)
                {
                    // Only sent when the opponent leaves before the start: no winner, no Elo change.
                    EndMatch("Match cancelled: your opponent left before the start");
                    await Disconnect();
                }
                break;

            case "ERROR":
                Log("Server: " + (f.Length > 1 ? f[1] : ""));
                break;
        }
    }

    // ------------------------------------------------------------------ misc

    private static int ParseInt(string text)
    {
        int value;
        return int.TryParse(text, out value) ? value : 0;
    }

    private static int ParseSigned(string text)
    {
        int value;
        return int.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out value) ? value : 0;
    }

    private static string FormatTime(string milliseconds)
    {
        int total = ParseInt(milliseconds) / 1000;
        return (total / 60) + ":" + (total % 60).ToString("00");
    }

    private void SetStatus(string text)
    {
        statusLabel.Text = text;
    }

    private void Log(string text)
    {
        logBox.Items.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + text);
        logBox.TopIndex = logBox.Items.Count - 1;
    }
}
