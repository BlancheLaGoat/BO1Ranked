using System.Diagnostics;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
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

    // Public GitHub repository whose latest release holds the newest BO1Ranked.exe (see CheckForUpdate).
    private const string UpdateRepo = "BlancheLaGoat/BO1Ranked";
    private const string ExeName = "BO1Ranked.exe";

    private static readonly Version AppVersion = Assembly.GetExecutingAssembly().GetName().Version;
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
    private static readonly string rankedDir = Path.Combine(plutoniumDir, "storage", "t5", "ranked");

    private static readonly string settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BO1Ranked", "settings.txt");

    // Present only when this app downloaded the plugin itself: uninstall then removes it too.
    private static readonly string pluginMarkerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BO1Ranked", "plugin-installed-by-app");

    private readonly TextBox nameBox = new TextBox();
    private readonly Label modLabel = new Label();
    private readonly Button installButton = new Button();
    private readonly Button uninstallButton = new Button();
    private readonly Button searchButton = new Button();
    private readonly Button testButton = new Button();
    private readonly Button stopButton = new Button();
    private readonly Label statusLabel = new Label();
    private readonly Label matchLabel = new Label();
    private readonly Label opponentLabel = new Label();
    private readonly ListBox logBox = new ListBox();
    private readonly System.Windows.Forms.Timer pollTimer = new System.Windows.Forms.Timer();

    private ClientWebSocket socket;
    private CancellationTokenSource socketCancel;
    private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);

    private bool modInstalled;
    private bool inMatch;
    private bool testMode;
    private int matchSeed;
    private int matchGoal;
    private string lastStateSent = "";
    private string pendingOpponentLine;      // line to write to opponent.txt (retried if the file is busy)
    private DateTime testStart;
    private bool testBotRunning;
    private bool testBotFinished;
    private bool readySent;                  // this player pressed "ready" in game and the server knows
    private bool goGiven;                    // go.txt written: the countdown is running or the race is on

    public MainForm()
    {
        Text = "BO1 Ranked v" + AppVersion.ToString(3);
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(460, 506);

        var nameLabel = new Label { Text = "Name", Location = new Point(16, 18), AutoSize = true };
        nameBox.Location = new Point(90, 15);
        nameBox.Size = new Size(354, 25);
        nameBox.MaxLength = 24;

        modLabel.Location = new Point(16, 54);
        modLabel.Size = new Size(428, 22);
        modLabel.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);

        installButton.Text = "Install mod files";
        installButton.Location = new Point(16, 80);
        installButton.Size = new Size(210, 30);
        installButton.Click += async (s, e) => await InstallClicked();

        uninstallButton.Text = "Uninstall mod files";
        uninstallButton.Location = new Point(234, 80);
        uninstallButton.Size = new Size(210, 30);
        uninstallButton.Click += (s, e) => UninstallClicked();

        searchButton.Text = "Find a match";
        searchButton.Location = new Point(16, 126);
        searchButton.Size = new Size(428, 44);
        searchButton.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
        searchButton.Click += async (s, e) => await SearchClicked();

        testButton.Text = "Test mode (bot opponent, round " + TestGoal + ")";
        testButton.Location = new Point(16, 178);
        testButton.Size = new Size(280, 30);
        testButton.Click += (s, e) => StartTestMatch();

        stopButton.Text = "Leave match";
        stopButton.Location = new Point(304, 178);
        stopButton.Size = new Size(140, 30);
        stopButton.Enabled = false;
        stopButton.Click += async (s, e) => await StopClicked();

        statusLabel.Location = new Point(16, 222);
        statusLabel.Size = new Size(428, 24);
        statusLabel.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        statusLabel.Text = "Ready";

        matchLabel.Location = new Point(16, 250);
        matchLabel.Size = new Size(428, 22);

        opponentLabel.Location = new Point(16, 274);
        opponentLabel.Size = new Size(428, 22);

        logBox.Location = new Point(16, 306);
        logBox.Size = new Size(428, 184);
        logBox.IntegralHeight = false;
        logBox.HorizontalScrollbar = true;

        Controls.AddRange(new Control[]
        {
            nameLabel, nameBox, modLabel, installButton, uninstallButton,
            searchButton, testButton, stopButton, statusLabel, matchLabel, opponentLabel, logBox
        });

        pollTimer.Interval = 500;
        pollTimer.Tick += async (s, e) => await PollTick();

        LoadSettings();
        FormClosing += (s, e) => { SaveSettings(); DeleteRankedFile("match.txt"); };

        if (!Directory.Exists(plutoniumDir))
        {
            Log("Plutonium folder not found: " + plutoniumDir);
        }
        RefreshModStatus();
        UpdateInstalledScripts();

        Shown += async (s, e) => await CheckForUpdate();
    }

    // ------------------------------------------------------------------ self-update

    // Looks at the latest GitHub release. If its tag (v1.2.3) is newer than this exe, downloads the new
    // exe, swaps it in place of the running one and restarts. A running exe cannot be overwritten on
    // Windows but it can be renamed, hence the ".old" file, deleted on the next start.
    private async Task CheckForUpdate()
    {
        string exePath = Environment.ProcessPath;
        if (exePath == null || Path.GetFileName(exePath) != ExeName) return;
        if (exePath.Contains(@"\bin\")) return;      // started from source (Run-Dev.bat): never self-replace

        try { File.Delete(exePath + ".old"); } catch { }

        try
        {
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(120);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("BO1Ranked");

                string json = await http.GetStringAsync("https://api.github.com/repos/" + UpdateRepo + "/releases/latest");
                Match tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9]+(\\.[0-9]+){1,3})\"");
                if (!tag.Success) return;

                Version latest = Version.Parse(tag.Groups[1].Value);
                if (latest <= AppVersion) return;
                if (inMatch || socket != null) return;      // never restart in the middle of a search or a match

                SetStatus("Updating to v" + latest + "...");
                Log("New version v" + latest + " found, downloading...");
                searchButton.Enabled = false;
                testButton.Enabled = false;
                installButton.Enabled = false;
                uninstallButton.Enabled = false;

                byte[] exe = await http.GetByteArrayAsync(
                    "https://github.com/" + UpdateRepo + "/releases/latest/download/" + ExeName);
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
        catch (Exception ex)
        {
            Log("Update check failed: " + ex.Message);
            SetStatus("Ready");
            SetIdleButtons();
        }
    }

    // ------------------------------------------------------------------ settings

    private void LoadSettings()
    {
        nameBox.Text = Environment.UserName;
        try
        {
            if (File.Exists(settingsPath))
            {
                string[] lines = File.ReadAllLines(settingsPath);
                if (lines.Length > 0 && lines[0].Trim() != "") nameBox.Text = lines[0].Trim();
            }
        }
        catch (Exception ex)
        {
            Log("Could not read settings: " + ex.Message);
        }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
            File.WriteAllLines(settingsPath, new[] { nameBox.Text.Trim() });
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
            modLabel.ForeColor = Color.FromArgb(0, 128, 0);
            installButton.Text = "Reinstall mod files";
        }
        else if (present > 0 || plugin)
        {
            modLabel.Text = "Mod files: update needed";
            modLabel.ForeColor = Color.FromArgb(200, 110, 0);
            installButton.Text = "Update mod files";
        }
        else
        {
            modLabel.Text = "Mod files: not installed";
            modLabel.ForeColor = Color.FromArgb(190, 0, 0);
            installButton.Text = "Install mod files";
        }
        uninstallButton.Enabled = present > 0;
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
        text.AppendLine("    " + PluginFile + "  (only if this app installed it)");
        text.AppendLine();
        text.AppendLine("Files that were backed up during install are put back.");
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
            if (File.Exists(pluginMarkerPath))
            {
                RemoveAndRestore(Path.Combine(pluginsDir, PluginFile));
                File.Delete(pluginMarkerPath);
            }
            else
            {
                Log("Kept " + PluginFile + " (it was not installed by this app)");
            }
            SetStatus("Mod files removed");
        }
        catch (Exception ex)
        {
            Log("Uninstall failed: " + ex.Message);
            SetStatus("Uninstall failed (is the game still running?)");
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

    private bool RequireMod()
    {
        RefreshModStatus();
        if (modInstalled) return true;

        MessageBox.Show(this,
            "The mod files are missing or out of date.\nClick \"" + installButton.Text + "\" first.",
            "BO1 Ranked", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
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

        if (!RequireMod()) return;

        SaveSettings();
        testButton.Enabled = false;
        installButton.Enabled = false;
        uninstallButton.Enabled = false;
        searchButton.Text = "Cancel search";
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

        string name = nameBox.Text.Replace(";", "").Trim();
        if (name == "") name = "Player";
        await SendLine("HELLO;" + name + ";" + AppVersion.ToString(3));
        await SendLine("QUEUE");
    }

    private void StartTestMatch()
    {
        if (inMatch || socket != null) return;
        if (!RequireMod()) return;

        testMode = true;
        testBotRunning = false;
        testBotFinished = false;
        BeginMatch(new Random().Next(1, 1000000), TestGoal, "Test bot");
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
        searchButton.Text = "Find a match";
        testButton.Enabled = true;
        stopButton.Enabled = false;
        installButton.Enabled = true;
        RefreshModStatus();
    }

    // ------------------------------------------------------------------ match flow

    private void BeginMatch(int seed, int goal, string opponentName)
    {
        inMatch = true;
        matchSeed = seed;
        matchGoal = goal;
        lastStateSent = "";
        pendingOpponentLine = null;
        readySent = false;
        goGiven = false;

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
        searchButton.Text = "Find a match";
        testButton.Enabled = false;
        installButton.Enabled = false;
        uninstallButton.Enabled = false;
        stopButton.Enabled = true;

        SetStatus("Match found: start Kino (solo), then ready up in game");
        matchLabel.Text = "Opponent: " + opponentName + "   |   seed " + seed + "   |   goal: round " + goal;
        opponentLabel.Text = "";
        Log("Match vs " + opponentName + ", seed " + seed);

        pollTimer.Start();
    }

    private void EndMatch(string status)
    {
        inMatch = false;
        testMode = false;
        pollTimer.Stop();
        DeleteRankedFile("match.txt");      // also frees a player still waiting at spawn
        DeleteRankedFile("go.txt");

        SetIdleButtons();
        SetStatus(status);
        Log(status);
    }

    private async Task PollTick()
    {
        if (!inMatch) return;

        FlushOpponentFile();

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
                break;

            case "WAITING":
                SetStatus("Looking for an opponent...");
                break;

            case "MATCH":
                if (f.Length >= 4)
                {
                    testMode = false;
                    BeginMatch(ParseInt(f[1]), ParseInt(f[2]), f[3]);
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
                    FlushOpponentFile();
                    if (f[1] == "WIN") EndMatch("VICTORY in " + FormatTime(f[2]));
                    else EndMatch("DEFEAT: opponent finished in " + FormatTime(f[3]));
                    await Disconnect();
                }
                break;

            case "OPPLEFT":
                if (inMatch)
                {
                    EndMatch("VICTORY by forfeit: opponent left");
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
