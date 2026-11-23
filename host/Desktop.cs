using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Record = System.Collections.Generic.Dictionary<string, object>;

sealed class Desktop
{
    readonly Window window;
    readonly string dataDir;
    readonly string appDir;
    readonly string marker;
    readonly HttpClient client = new HttpClient(new HttpClientHandler { UseProxy = false });
    readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
    readonly string[] sections = { "Home", "Prompts", "Sessions", "Accounts", "Devices", "Wifi", "Remote", "Feedback", "Settings" };
    readonly SolidColorBrush mint = Brush("#10B981");
    readonly SolidColorBrush blue = Brush("#38BDF8");
    readonly SolidColorBrush accent = Brush("#5B7CFA");
    readonly SolidColorBrush amber = Brush("#F59E0B");
    readonly SolidColorBrush coral = Brush("#F43F5E");
    readonly SolidColorBrush violet = Brush("#A78BFA");
    readonly SolidColorBrush muted = Brush("#8E9BB3");
    readonly SolidColorBrush textBrush = Brush("#F8FAFC");

    string lang = "en";
    string projectFilter = "";
    string reportKind = "bug";
    string updateState = "none", updateVersion = "", updateAssetUrl = "", updateAssetName = "";
    string section, deviceSignature, sessionSignature;
    string pendingScanDevice, pendingWifiDevice, pendingWifiId, wifiSelection;
    int pendingRevision, operations;
    int onboardingStep = 1;
    bool suppressAutostartEvent;
    bool stickFwDismissed;
    bool shutdownAfter;
    bool powerArmed;
    int cooldownSecs = -1;
    string appVersion = "";
    DateTime scanUntil, wifiUntil, loginWatchUntil = DateTime.MinValue;
    int knownCodexCount = -1;
    bool ticking;
    int sessionTab = 0; // 0 = Output, 1 = Live Activity
    Record loadedPrompt = new Record(), linkSnapshot = new Record(), lastDevicesResult = new Record();
    List<Record> currentPromptRows = new List<Record>();
    List<Record> currentHomeModels = new List<Record>();
    List<Record> homeChainDraft = new List<Record>();
    Point dragStartPoint;

    static readonly Dictionary<string, SolidColorBrush> BrushCache = new Dictionary<string, SolidColorBrush>();
    string cachedPort = "";
    string cachedToken = "";
    static readonly Dictionary<string, Dictionary<string, string>> Strings = BuildStrings();

    [STAThread]
    static void Main(string[] args)
    {
        bool validate = args.Contains("--validate");
        try
        {
            string data;
            string layout;
            ResolvePaths(args, out data, out layout);
            if (!validate) EnsureBridgeRunning(data, layout);
            var app = new Application();
            var desktop = new Desktop(data, layout);
            if (validate) return;
            app.Run(desktop.window);
        }
        catch (Exception error)
        {
            if (validate)
            {
                Console.Error.WriteLine(error);
                Environment.ExitCode = 1;
            }
            else MessageBox.Show(error.Message, "AgentDeck", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    static void ResolvePaths(string[] args, out string data, out string layout)
    {
        var positional = args.Where(a => !a.StartsWith("--")).ToArray();
        if (positional.Length >= 2)
        {
            data = positional[0];
            layout = positional[1];
            return;
        }
        var exeDir = Path.GetDirectoryName(typeof(Desktop).Assembly.Location) ?? AppDomain.CurrentDomain.BaseDirectory;
        var hostLayout = Path.Combine(exeDir, "host", "Window.xaml");
        if (!File.Exists(hostLayout))
        {
            var parent = Path.GetDirectoryName(exeDir);
            if (!string.IsNullOrEmpty(parent) && File.Exists(Path.Combine(parent, "host", "Window.xaml")))
                hostLayout = Path.Combine(parent, "host", "Window.xaml");
        }
        if (!File.Exists(hostLayout))
            throw new FileNotFoundException("Window.xaml not found at " + hostLayout);
        layout = hostLayout;
        data = Directory.Exists("D:\\")
            ? "D:\\AgentDeck-data"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentDeck");
    }

    static void EnsureBridgeRunning(string data, string layout)
    {
        Directory.CreateDirectory(data);
        var markerFile = Path.Combine(data, "desktop.json");
        var aliveFile = Path.Combine(data, "bridge-alive.json");
        if (File.Exists(aliveFile) && File.Exists(markerFile))
        {
            try
            {
                var serializer = new JavaScriptSerializer();
                var aliveDoc = serializer.Deserialize<Record>(File.ReadAllText(aliveFile));
                var pid = Convert.ToInt32(Get(aliveDoc, "pid"));
                var proc = Process.GetProcessById(pid);
                if (!proc.HasExited) return;
            }
            catch { }
        }

        var rootDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(layout)));
        var cliScript = Path.Combine(rootDir, "host", "agentdeck.py");
        if (!File.Exists(cliScript))
            throw new FileNotFoundException("agentdeck.py not found at " + cliScript);

        string pythonExe = FindPythonRunner(rootDir);
        var start = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = "\"" + cliScript + "\" --data \"" + data + "\" --allow-shutdown",
            WorkingDirectory = rootDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.EnvironmentVariables["PYTHONUTF8"] = "1";
        start.EnvironmentVariables["PYTHONDONTWRITEBYTECODE"] = "1";
        var child = Process.Start(start);

        for (int i = 0; i < 45; i++)
        {
            if (child != null && child.HasExited)
                throw new InvalidOperationException("AgentDeck bridge failed to start. Check " + Path.Combine(data, "bridge-errors.log"));
            if (File.Exists(markerFile) && File.Exists(aliveFile)) return;
            Thread.Sleep(150);
        }
    }

    static string FindPythonRunner(string rootDir)
    {
        string[] candidates = {
            @"D:\AgentDeck-tools\venv\Scripts\pythonw.exe",
            Path.Combine(rootDir, ".venv", "Scripts", "pythonw.exe"),
            @"D:\AgentDeck-tools\venv\Scripts\python.exe",
            Path.Combine(rootDir, ".venv", "Scripts", "python.exe"),
            "pythonw.exe",
            "python.exe"
        };
        foreach (var path in candidates)
        {
            if (!Path.IsPathRooted(path) || File.Exists(path)) return path;
        }
        return "python.exe";
    }

    Desktop(string data, string layout)
    {
        dataDir = data;
        appDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(layout)));
        marker = Path.Combine(data, "desktop.json");
        using (var file = File.OpenRead(layout)) window = (Window)XamlReader.Load(file);
        client.Timeout = TimeSpan.FromSeconds(25);
        LoadWindowIcon();
        LoadBrandLogo();

        C<ComboBox>("Provider").SelectedIndex = 0;
        C<ComboBox>("LanguagePicker").SelectedIndex = 0;

        // Top Chrome controls
        C<Button>("WinMinBtn").Click += (s, e) => { window.WindowState = WindowState.Minimized; };
        C<Button>("WinMaxBtn").Click += (s, e) =>
        {
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        };
        C<Button>("WinCloseBtn").Click += (s, e) => window.Close();
        window.StateChanged += (s, e) =>
        {
            var max = window.WindowState == WindowState.Maximized;
            window.MaxHeight = max ? SystemParameters.WorkArea.Height + 14 : double.PositiveInfinity;
            window.MaxWidth = max ? SystemParameters.WorkArea.Width + 14 : double.PositiveInfinity;
            window.Padding = new Thickness(max ? 7 : 0);
            var box = C<Border>("MainContainer");
            box.CornerRadius = new CornerRadius(max ? 0 : 14);
            box.BorderThickness = new Thickness(max ? 0 : 1);
            var maxIcon = C<System.Windows.Shapes.Path>("WinMaxIcon");
            if (maxIcon != null)
            {
                maxIcon.Data = Geometry.Parse(max
                    ? "M3,1.5 H9.5 V8 M1.5,3.5 H8 V9.5 H1.5 Z"
                    : "M1.5,1.5 H9.5 V9.5 H1.5 Z");
            }
        };

        Button("TitleJobsBtn", () => Navigate("Sessions"));
        Action("TitleShutdownBtn", "toggling_shutdown", ToggleShutdownAfter);
        Action("CancelShutdownBtn", "cancelling_shutdown", CancelShutdownNow);
        Action("SessionAutoOffBtn", "toggling_shutdown", ToggleShutdownAfter);

        foreach (var name in sections)
        {
            var page = name;
            C<Button>("Nav" + name).Click += (sender, e) => Navigate(page);
        }

        // Home Quick Prompt + Chain controls
        C<ComboBox>("HomeProvider").SelectionChanged += async (s, e) => await LoadHomeModels();
        C<ComboBox>("HomeModel").SelectionChanged += (s, e) => UpdateHomeEfforts();
        Button("HomeStageChainBtn", StageHomeChainStep);
        Button("HomeClearChainBtn", () => { homeChainDraft.Clear(); RefreshHomeChainBanner(); });
        Action("HomeSendBtn", "sending_home_prompt", SendHomePrompt);
        Button("HomeOpenSessionsBtn", () => Navigate("Sessions"));
        Button("HomeGoSessionsBtn", () => Navigate("Sessions"));
        Action("RefreshIdeBtn", "checking_ides", () => RefreshIdeStatus(true));
        Action("InstallAgyBtn", "installing_agy", () => InstallIde("agy"));
        Action("InstallCodexBtn", "installing_codex", () => InstallIde("codex"));
        Action("HomeFlashUsbBtn", "stick_fw_flashing", FlashStickFirmware);
        Action("HomePairUsbBtn", "pairing_usb", OnboardingPairUsb);

        // Projects & Prompts + Drag-and-Drop Reorder
        Button("FilterAll", () => SetProjectFilter(""));
        Button("FilterAgy", () => SetProjectFilter("agy"));
        Button("FilterCodex", () => SetProjectFilter("codex"));
        C<ListBox>("ProjectList").SelectionChanged += async (sender, e) => await ReadPrompts();
        C<ListBox>("PromptList").SelectionChanged += (sender, e) =>
        {
            var prompt = Selection("PromptList");
            if (prompt.Count != 0) FillPrompt(prompt);
        };
        WirePromptDragAndDrop();
        Action("MovePromptUpBtn", "reordering_prompts", () => MoveSelectedPrompt(-1));
        Action("MovePromptDownBtn", "reordering_prompts", () => MoveSelectedPrompt(1));
        Button("NewPrompt", () => { C<ListBox>("PromptList").SelectedIndex = -1; FillPrompt(new Record()); });
        Action("SavePrompt", "saving_prompt", SavePrompt);
        Action("DeletePrompt", "deleting_prompt", DeletePrompt);
        Action("NewProjectBtn", "creating_project", NewProject);
        Action("RenameProjectBtn", "renaming_project", RenameProject);
        Action("AddProject", "select_project_folder", AddProject);
        Action("BindProject", "bind_agy_title", BindProject);
        Action("ShowProject", "sending_project", ShowProject);
        Button("UseProject", UseStickProject);

        // Sessions & Live Inspector
        C<ListBox>("SessionList").SelectionChanged += async (sender, e) => await ReadSession();
        Button("TabOutputBtn", () => SwitchSessionTab(0));
        Button("TabActivityBtn", () => SwitchSessionTab(1));
        Action("StopSessionBtn", "stopping_session", StopSelectedSession);
        Action("RetrySessionBtn", "retrying_session", RetrySelectedSession);
        Action("ChainNextBtn", "chaining_next", ChainNextToSession);
        Action("CancelChainBtn", "cancelling_chain", CancelSelectedChain);
        C<Button>("OpenConversation").Click += async (sender, e) =>
        {
            var id = Str(Selected("SessionList"), "id");
            C<Button>("OpenConversation").IsEnabled = false;
            await Work(L("opening_conv"), async () =>
            {
                await Api("open_conversation", "job", id);
                Note(L("conv_opened"));
            });
            if (id == Str(Selected("SessionList"), "id")) C<Button>("OpenConversation").IsEnabled = true;
        };
        Button("OpenAnswer", () =>
        {
            var path = C<Button>("OpenAnswer").Tag as string;
            if (string.IsNullOrEmpty(path)) return;
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception error) { Note(error.Message, true); }
        });
        Button("CopyOutputBtn", () =>
        {
            var txt = C<TextBox>("SessionAnswer").Text;
            if (string.IsNullOrEmpty(txt)) return;
            try { Clipboard.SetText(txt); Note(L("copied_output")); }
            catch (Exception error) { Note(error.Message, true); }
        });
        Action("DeleteSessionBtn", "deleting_session", DeleteSelectedSession);
        Action("ClearSessionsBtn", "clearing_sessions", ClearFinishedSessions);

        // Accounts
        C<ListBox>("CodexAccountList").AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnCodexListButtonClick));
        Action("LoginCodexAccount", "launching_login", LoginCodexAccount);
        Action("RefreshAccountsBtn", "loading_accounts", () => RefreshAccounts(false));
        Action("RefreshAgyBtn", "loading_accounts", () => RefreshAccounts(true));

        // Devices & Wi-Fi
        C<ListBox>("DeviceList").SelectionChanged += (sender, e) =>
        {
            var row = Selection("DeviceList");
            if (Str(row, "address").Length > 0) C<TextBox>("DeviceAddress").Text = Str(row, "address");
        };
        C<ComboBox>("WifiDevice").SelectionChanged += (sender, e) =>
        {
            var device = DeviceId();
            if (device.Length == 0 || device == wifiSelection) return;
            wifiSelection = device;
            C<ListBox>("NetworkList").ItemsSource = null;
            pendingScanDevice = pendingWifiDevice = null;
        };
        C<ListBox>("NetworkList").SelectionChanged += (sender, e) =>
        {
            var selected = Selection("NetworkList");
            C<TextBlock>("NetworkName").Text = selected.Count == 0 ? L("select_network") : Str(selected, "ssid");
            C<PasswordBox>("WifiPassword").Clear();
            C<PasswordBox>("WifiPassword").IsEnabled = Flag(selected, "secured");
        };
        Action("ShowCode", "opening_pairing", async () =>
        {
            await Api("show_pairing");
            Note(L("enter_stick_code"));
        });
        Action("PairUsb", "pairing_usb", () => Pair("pair_usb", false));
        Action("PairUsbCode", "checking_usb_code", () => Pair("pair_usb", true));
        Action("PairLan", "checking_wifi_code", () => Pair("pair_lan", true));
        Action("FindDevices", "scanning_lan", Discover);
        Action("ForgetDevice", "removing_pairing", Forget);
        Action("FlashStickBtn", "stick_fw_flashing", FlashStickFirmware);
        Action("ScanWifi", "scanning_wifi", ScanWifi);
        Action("JoinWifi", "sending_wifi", JoinWifi);

        // Remote Control & Relay
        Action("CodexRcStart", "rc_working", () => RemoteControlAction("codex", "start"));
        Action("CodexRcStop", "rc_working", () => RemoteControlAction("codex", "stop"));
        Action("CodexRcPair", "rc_working", () => RemoteControlAction("codex", "pair"));
        Action("AgyRcStart", "rc_working", () => RemoteControlAction("agy", "start"));
        Action("AgyRcStop", "rc_working", () => RemoteControlAction("agy", "stop"));
        Action("RefreshRemoteBtn", "rc_working", RefreshRemoteControl);
        Action("StartLocalRelay", "starting_local_relay", StartLocalRelay);
        Action("StopLocalRelay", "stopping_local_relay", StopLocalRelay);
        Action("SaveRelay", "saving_relay", () => SaveRelay(C<TextBox>("RelayAddress").Text));
        Action("ClearRelay", "clearing_relay", () => SaveRelay(""));
        Action("SyncRelay", "syncing_stick", async () =>
        {
            await Api("relay_sync", "device", DeviceId());
            Note(L("relay_synced"));
        });
        Action("ExportChannels", "exporting_channels", ExportChannels);
        Action("TestRelay", "testing_relay", TestRelay);

        // Settings & Feedback
        Action("SaveLanguageBtn", "applying_language", ApplySelectedLanguage);
        Button("BrowseProjectsRoot", BrowseProjectsRoot);
        Action("SaveProjectsRoot", "saving_settings", SaveProjectsRoot);
        Action("CheckUpdateBtn", "checking_updates", () => CheckForUpdateAsync(false));
        Button("OpenWizardBtn", () => ShowOnboarding(1));
        Button("OpenDataFolderBtn", () => OpenFolder(dataDir));
        Button("OpenAppFolderBtn", () => OpenFolder(appDir));
        C<CheckBox>("AutostartToggle").Checked += async (s, e) => await OnAutostartToggled(true);
        C<CheckBox>("AutostartToggle").Unchecked += async (s, e) => await OnAutostartToggled(false);
        Button("ReportCatBug", () => SetReportKind("bug"));
        Button("ReportCatFeature", () => SetReportKind("feature"));
        Button("ReportCatOther", () => SetReportKind("other"));
        Action("SendReportBtn", "sending_report", SubmitFeedbackReport);

        // Banners & Overlays
        C<Button>("UpdateActionBtn").Click += async (s, e) => await HandleUpdateBannerClick();
        Button("UpdateDismissBtn", HideUpdateBanner);
        Action("StickFwUpdateBtn", "stick_fw_flashing", FlashStickFirmware);
        Button("StickFwDismissBtn", () => { stickFwDismissed = true; C<Border>("StickFwBanner").Visibility = Visibility.Collapsed; });
        Action("OnbFlashUsbBtn", "stick_fw_flashing", FlashStickFirmware);
        Action("AfterUpdateFlashBtn", "stick_fw_flashing", FlashStickFirmware);
        C<Button>("AfterUpdateCloseBtn").Click += async (s, e) => await CloseAfterUpdate();
        C<Button>("OnbLangEnBtn").Click += async (s, e) => await PickOnboardingLanguage("en");
        C<Button>("OnbLangPlBtn").Click += async (s, e) => await PickOnboardingLanguage("pl");
        Button("OnbBackBtn", () => { if (onboardingStep > 1) ShowOnboarding(onboardingStep - 1); });
        Action("OnbPairUsbBtn", "pairing_usb", OnboardingPairUsb);
        C<Button>("OnbNextBtn").Click += async (s, e) => await AdvanceOnboarding();
        C<Button>("OnbSkipBtn").Click += async (s, e) => await FinishOnboarding();

        ApplyLanguage();
        UpdateFilterButtons();
        SwitchSessionTab(0);
        SetReportKind("bug");

        window.Activated += async (s, e) =>
        {
            if (section == "Accounts" || DateTime.UtcNow < loginWatchUntil)
            {
                try { await RefreshAccounts(false); } catch { }
            }
        };

        window.Loaded += async (sender, e) =>
        {
            double maxH = Math.Max(640, SystemParameters.WorkArea.Height - 24);
            double maxW = Math.Max(1000, SystemParameters.WorkArea.Width - 24);
            if (window.Height > maxH) window.Height = maxH;
            if (window.Width > maxW) window.Width = maxW;
            StartAmbientAnimations();
            AnimateEntrance(C<FrameworkElement>("MainContainer"));
            ApplyLanguage();
            UpdateFilterButtons();
            SwitchSessionTab(0);
            SetReportKind("bug");
            C<ComboBox>("HomeMode").SelectedIndex = 0;
            Navigate("Home");
            await Work(L("loading_projects"), async () =>
            {
                var settings = await Api("settings_get");
                lang = Str(settings, "language") == "pl" ? "pl" : "en";
                C<ComboBox>("LanguagePicker").SelectedIndex = lang == "pl" ? 1 : 0;
                C<TextBox>("ProjectsRootInput").Text = Str(settings, "projects_root");
                var onboarded = Flag(settings, "onboarded");
                UpdateAutostartUi(Flag(settings, "autostart"), onboarded);
                appVersion = Str(settings, "version");
                var lastSeen = Str(settings, "last_seen_version");
                var dver = Str(settings, "display_version");
                if (dver.Length > 0) C<TextBlock>("VersionBadge").Text = dver;
                ApplyLanguage();
                await ReadProjects("");
                await RefreshIdeStatus(false);
                var relay = await Api("relay_get");
                C<TextBox>("RelayAddress").Text = Str(relay, "endpoint");
                UpdateLocalRelayBadge(Obj(relay, "local_relay"));
                if (!onboarded) ShowOnboarding(1);
                else if (appVersion.Length > 0 && lastSeen != appVersion) ShowAfterUpdate();
                Note(L("ready"));
            });
            timer.Tick += async (s2, e2) => await RefreshState();
            timer.Start();
            await RefreshState();
            var updateCheck = CheckForUpdateQuietly();
        };
        window.Closed += (sender, e) => { timer.Stop(); client.Dispose(); };
    }

    void AnimateEntrance(FrameworkElement element)
    {
        if (!SystemParameters.ClientAreaAnimation || element == null) return;
        var duration = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var transform = new TranslateTransform();
        element.RenderTransform = transform;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, duration) { EasingFunction = ease });
    }

    void LoadWindowIcon()
    {
        string[] candidates = {
            Path.Combine(appDir, "assets", "logo.ico"),
            Path.Combine(appDir, "logo.ico"),
            Path.Combine(appDir, "assets", "logo.png")
        };
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try { window.Icon = BitmapFrame.Create(new Uri(path, UriKind.Absolute)); return; }
            catch { }
        }
    }

    void LoadBrandLogo()
    {
        string[] candidates = {
            Path.Combine(appDir, "assets", "logo.png"),
            Path.Combine(appDir, "logo.png"),
            Path.Combine(dataDir, "logo.png")
        };
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                C<Image>("BrandLogo").Source = bmp;
                C<Image>("BrandLogo").Visibility = Visibility.Visible;
                C<FrameworkElement>("BrandFallback").Visibility = Visibility.Collapsed;
                return;
            }
            catch { }
        }
    }

    void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error) { Note(error.Message, true); }
    }

    T C<T>(string name) where T : FrameworkElement { return (T)window.FindName(name); }
    static SolidColorBrush Brush(string value)
    {
        SolidColorBrush brush;
        if (BrushCache.TryGetValue(value, out brush)) return brush;
        brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        BrushCache[value] = brush;
        return brush;
    }
    static object Get(Record row, string key) { object value; return row != null && row.TryGetValue(key, out value) ? value : null; }
    static string Str(Record row, string key) { return Convert.ToString(Get(row, key)) ?? ""; }
    static bool Flag(Record row, string key) { return Get(row, key) is bool && (bool)Get(row, key); }
    static int IntVal(Record row, string key, int fallback = -1)
    {
        var val = Get(row, key);
        if (val == null) return fallback;
        try { return Convert.ToInt32(val); }
        catch { return fallback; }
    }
    static Record Obj(Record row, string key) { return Get(row, key) as Record ?? new Record(); }
    static object[] Items(Record row, string key)
    {
        var items = Get(row, key) as IEnumerable;
        return items == null ? new object[0] : items.Cast<object>().ToArray();
    }
    static Record[] Rows(Record row, string key) { return Items(row, key).OfType<Record>().ToArray(); }
    static string Display(Record row, string key, string fallback) { var value = Str(row, key); return value.Length > 0 ? value : fallback; }

    string L(string key)
    {
        Dictionary<string, string> table;
        string val;
        if (Strings.TryGetValue(lang, out table) && table.TryGetValue(key, out val)) return val;
        if (Strings["en"].TryGetValue(key, out val)) return val;
        return key;
    }

    Record Selection(string name)
    {
        var selector = (Selector)window.FindName(name);
        var item = selector.SelectedItem as Choice;
        return item == null ? new Record() : item.Value;
    }
    Record Selected(string name) { return Selection(name); }

    sealed class Choice
    {
        public readonly Record Value;
        public string Id { get; private set; }
        public string Title { get; private set; }
        public string Path { get; private set; }
        public string Badge { get; private set; }
        public SolidColorBrush BadgeBg { get; private set; }
        public SolidColorBrush BadgeFg { get; private set; }
        public Visibility BadgeVisibility { get { return string.IsNullOrEmpty(Badge) ? Visibility.Collapsed : Visibility.Visible; } }
        public string ActionLabel { get; private set; }
        public Visibility ActionVisibility { get { return string.IsNullOrEmpty(ActionLabel) ? Visibility.Collapsed : Visibility.Visible; } }

        public Choice(Record value, string title, string path, string badge, SolidColorBrush bg, SolidColorBrush fg, string actionLabel = "")
        {
            Value = value;
            Id = Str(value, "id");
            Title = title;
            Path = path;
            Badge = badge ?? "";
            BadgeBg = bg ?? Brush("#161F33");
            BadgeFg = fg ?? Brush("#8E9BB3");
            ActionLabel = actionLabel ?? "";
        }
        public override string ToString() { return Title; }
    }

    void FillChoices(string name, IEnumerable<Record> rows, Func<Record, Choice> mapper, string id = "")
    {
        var selector = (Selector)window.FindName(name);
        var choices = rows.Select(mapper).ToArray();
        selector.ItemsSource = choices;
        if (id.Length > 0) selector.SelectedItem = choices.FirstOrDefault(row => Str(row.Value, "id") == id);
    }

    void Fill(string name, IEnumerable<Record> rows, Func<Record, string> title, string id = "")
    {
        FillChoices(name, rows, row => new Choice(row, title(row), Str(row, "path"), "", null, null), id);
    }

    async Task<Record> Api(string op, params object[] fields)
    {
        var api = new JavaScriptSerializer { MaxJsonLength = 2000000 };
        var data = new Record { { "op", op } };
        for (int i = 0; i < fields.Length; i += 2) data.Add((string)fields[i], fields[i + 1]);
        if (cachedPort.Length == 0 || cachedToken.Length == 0)
        {
            var settings = api.Deserialize<Record>(File.ReadAllText(marker));
            var key = ProtectedData.Unprotect(Convert.FromBase64String(Str(settings, "token")), null, DataProtectionScope.CurrentUser);
            cachedPort = Str(settings, "port");
            cachedToken = Encoding.UTF8.GetString(key);
        }
        try
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:" + cachedPort + "/desktop"))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cachedToken);
                request.Content = new StringContent(api.Serialize(data), Encoding.UTF8, "application/json");
                using (var reply = await client.SendAsync(request))
                {
                    reply.EnsureSuccessStatusCode();
                    var result = api.Deserialize<Record>(await reply.Content.ReadAsStringAsync());
                    if (!Flag(result, "ok")) throw new InvalidOperationException(Str(result, "error"));
                    return result;
                }
            }
        }
        catch (HttpRequestException)
        {
            cachedPort = "";
            cachedToken = "";
            throw;
        }
    }

    void Navigate(string name)
    {
        section = name;
        foreach (var page in sections)
        {
            var button = C<Button>("Nav" + page);
            var active = page == name;
            button.Background = active ? Brush("#172446") : Brushes.Transparent;
            button.BorderBrush = active ? Brush("#3D5EB5") : Brushes.Transparent;
            C<TextBlock>("Nav" + page + "Text").Foreground = active ? textBrush : muted;
            C<FrameworkElement>(page).Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateHeading();
        var entry = C<FrameworkElement>(name);
        if (SystemParameters.ClientAreaAnimation && entry != null)
        {
            var time = TimeSpan.FromMilliseconds(200);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var transform = new TranslateTransform();
            entry.RenderTransform = transform;
            entry.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.25, 1, time) { EasingFunction = ease });
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, time) { EasingFunction = ease });
        }
        if (name == "Accounts") { var t = RefreshAccounts(false); }
        else if (name == "Remote") { var t = RefreshRemoteControl(); }
        else if (name == "Home") { var t = RefreshIdeStatus(false); }
    }

    void UpdateHeading()
    {
        if (string.IsNullOrEmpty(section)) return;
        C<TextBlock>("Heading").Text = C<TextBlock>("Nav" + section + "Text").Text;
        C<TextBlock>("SubHeading").Text = L("sub_" + section.ToLowerInvariant());
    }

    void Note(string text, bool error = false)
    {
        C<TextBlock>("Status").Text = text;
        C<TextBlock>("Status").Foreground = error ? coral : muted;
    }

    void StartAmbientAnimations()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var orb1 = window.FindName("BgOrb1") as FrameworkElement;
        var orb2 = window.FindName("BgOrb2") as FrameworkElement;
        if (orb1 != null)
        {
            var anim1 = new DoubleAnimation(0.26, 0.55, TimeSpan.FromSeconds(4.5))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            orb1.BeginAnimation(UIElement.OpacityProperty, anim1);
        }
        if (orb2 != null)
        {
            var anim2 = new DoubleAnimation(0.20, 0.45, TimeSpan.FromSeconds(5.6))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            orb2.BeginAnimation(UIElement.OpacityProperty, anim2);
        }
    }

    void SetSpinnerActive(bool active)
    {
        var spinner = window.FindName("ActivitySpinner") as FrameworkElement;
        var rot = window.FindName("SpinnerRotate") as RotateTransform;
        if (spinner == null) return;
        spinner.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (rot == null) return;
        if (active && SystemParameters.ClientAreaAnimation)
        {
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.85))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            rot.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
        else rot.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    async Task Work(string text, Func<Task> work)
    {
        operations++;
        C<ProgressBar>("Activity").Visibility = Visibility.Visible;
        C<ProgressBar>("Activity").IsIndeterminate = SystemParameters.ClientAreaAnimation;
        SetSpinnerActive(true);
        Note(text);
        try { await work(); }
        catch (Exception error) { Note(error.Message, true); }
        finally
        {
            if (--operations == 0)
            {
                C<ProgressBar>("Activity").Visibility = Visibility.Collapsed;
                SetSpinnerActive(false);
            }
        }
    }

    void Button(string name, Action work) { C<Button>(name).Click += (sender, e) => work(); }
    void Action(string name, string textKey, Func<Task> work)
    {
        C<Button>(name).Click += async (sender, e) =>
        {
            var button = (Button)sender;
            button.IsEnabled = false;
            try { await Work(L(textKey), work); }
            finally { button.IsEnabled = true; }
        };
    }

    string ProjectId() { return Str(Selected("ProjectList"), "id"); }
    string DeviceId() { return Str(Selected("WifiDevice"), "id"); }
    Record Snapshot() { return linkSnapshot; }
    string Scope() { return Convert.ToString(((ComboBoxItem)C<ComboBox>("Provider").SelectedItem).Tag); }

    // ========================
    // Auto-Shutdown UI & Logic
    // ========================
    void UpdatePowerUi(Record ov)
    {
        if (ov == null || ov.Count == 0) return;
        shutdownAfter = Flag(ov, "shutdown_after");
        powerArmed = Flag(ov, "armed");
        cooldownSecs = IntVal(ov, "cooldown", -1);
        var settling = Flag(ov, "shutdown_settling");
        var activeCount = IntVal(ov, "active", 0);
        var chainQueued = IntVal(ov, "chain_queued", 0);
        var powerSent = Flag(ov, "power_sent");

        // Top Titlebar pills
        C<Ellipse>("TitleJobsDot").Fill = activeCount > 0 ? blue : muted;
        C<TextBlock>("TitleJobsText").Text = activeCount > 0
            ? string.Format(L("title_active_fmt"), activeCount)
            : L("title_idle");
        C<TextBlock>("TitleJobsText").Foreground = activeCount > 0 ? textBrush : muted;

        var chBadge = C<Border>("TitleChainBadge");
        if (chainQueued > 0)
        {
            chBadge.Visibility = Visibility.Visible;
            C<TextBlock>("TitleChainText").Text = string.Format(L("title_chain_fmt"), chainQueued);
        }
        else chBadge.Visibility = Visibility.Collapsed;

        var offBtn = C<Button>("TitleShutdownBtn");
        var isAnyOff = shutdownAfter || powerArmed || settling;
        offBtn.Background = isAnyOff ? Brush("#2D200B") : Brush("#101829");
        offBtn.BorderBrush = isAnyOff ? amber : Brush("#24314E");
        C<Ellipse>("TitleShutdownDot").Fill = isAnyOff ? amber : muted;
        C<TextBlock>("TitleShutdownText").Foreground = isAnyOff ? amber : muted;
        C<TextBlock>("TitleShutdownText").Text = powerArmed && cooldownSecs >= 0
            ? string.Format(L("title_off_countdown"), cooldownSecs)
            : (shutdownAfter ? L("title_off_armed") : L("title_off_idle"));

        C<Button>("SessionAutoOffBtn").Content = isAnyOff ? L("btn_cancel_auto_off") : L("btn_auto_off");

        // Top Shutdown Banner
        var banner = C<Border>("ShutdownBanner");
        if (powerSent)
        {
            banner.Visibility = Visibility.Visible;
            C<TextBlock>("ShutdownBannerTitle").Text = L("shutdown_sent_title");
            C<TextBlock>("ShutdownBannerSub").Text = L("shutdown_sent_sub");
            C<ProgressBar>("ShutdownCountdownBar").Visibility = Visibility.Collapsed;
            C<Button>("CancelShutdownBtn").Visibility = Visibility.Collapsed;
        }
        else if (powerArmed && cooldownSecs >= 0)
        {
            banner.Visibility = Visibility.Visible;
            C<TextBlock>("ShutdownBannerTitle").Text = string.Format(L("shutdown_countdown_title"), cooldownSecs);
            C<TextBlock>("ShutdownBannerSub").Text = L("shutdown_countdown_sub");
            var bar = C<ProgressBar>("ShutdownCountdownBar");
            bar.Visibility = Visibility.Visible;
            bar.Value = Math.Max(0, Math.Min(30, cooldownSecs));
            C<Button>("CancelShutdownBtn").Visibility = Visibility.Visible;
        }
        else if (shutdownAfter || settling)
        {
            banner.Visibility = Visibility.Visible;
            C<TextBlock>("ShutdownBannerTitle").Text = settling ? L("shutdown_settling_title") : L("shutdown_armed_title");
            C<TextBlock>("ShutdownBannerSub").Text = L("shutdown_armed_sub");
            C<ProgressBar>("ShutdownCountdownBar").Visibility = Visibility.Collapsed;
            C<Button>("CancelShutdownBtn").Visibility = Visibility.Visible;
        }
        else
        {
            banner.Visibility = Visibility.Collapsed;
        }

        // Home Live Bridge card
        if (activeCount > 0)
        {
            C<TextBlock>("HomeLiveSummary").Text = string.Format(L("home_live_active_fmt"), activeCount, chainQueued) +
                (isAnyOff ? ("\n" + L("title_off_armed")) : "");
        }
        else if (isAnyOff)
        {
            C<TextBlock>("HomeLiveSummary").Text = L("shutdown_armed_sub");
        }
        else
        {
            C<TextBlock>("HomeLiveSummary").Text = L("home_live_idle");
        }
    }

    async Task ToggleShutdownAfter()
    {
        bool target = !(shutdownAfter || powerArmed);
        var res = await Api("shutdown_after", "enabled", target);
        UpdatePowerUi(res);
        Note(target ? L("shutdown_armed_note") : L("shutdown_cancelled_note"));
    }

    async Task CancelShutdownNow()
    {
        var res = await Api("shutdown_after", "enabled", false);
        UpdatePowerUi(res);
        Note(L("shutdown_cancelled_note"));
    }

    // ========================
    // Home Dashboard Models & Chain Staging
    // ========================
    async Task RefreshIdeStatus(bool force)
    {
        var res = await Api("ide_status", "force", force);
        var ides = Obj(res, "ides");
        var agy = Obj(ides, "agy");
        var codex = Obj(ides, "codex");
        UpdateIdeRow("Agy", agy);
        UpdateIdeRow("Codex", codex);

        var installedList = new List<Record>();
        if (Flag(agy, "installed")) installedList.Add(new Record { { "id", "agy" }, { "label", "Antigravity CLI" } });
        if (Flag(codex, "installed")) installedList.Add(new Record { { "id", "codex" }, { "label", "ChatGPT Codex" } });
        var prevProv = Str(Selected("HomeProvider"), "id");
        Fill("HomeProvider", installedList, r => Str(r, "label"), prevProv.Length > 0 ? prevProv : (installedList.Count > 0 ? Str(installedList[0], "id") : ""));
        if (C<ComboBox>("HomeProvider").SelectedIndex < 0 && installedList.Count > 0)
            C<ComboBox>("HomeProvider").SelectedIndex = 0;
        C<Button>("HomeSendBtn").IsEnabled = installedList.Count > 0;
        C<Button>("HomeStageChainBtn").IsEnabled = installedList.Count > 0;
        await LoadHomeModels();
    }

    async Task LoadHomeModels()
    {
        var prov = Str(Selected("HomeProvider"), "id");
        if (prov.Length == 0)
        {
            currentHomeModels.Clear();
            C<ComboBox>("HomeModel").ItemsSource = null;
            C<ComboBox>("HomeEffort").ItemsSource = null;
            return;
        }
        try
        {
            var res = await Api("models_for_provider", "provider", prov);
            currentHomeModels = Rows(res, "items").ToList();
            var prevModel = Str(Selected("HomeModel"), "id");
            Fill("HomeModel", currentHomeModels, r => Str(r, "label"), prevModel);
            if (C<ComboBox>("HomeModel").SelectedIndex < 0 && currentHomeModels.Count > 0)
                C<ComboBox>("HomeModel").SelectedIndex = 0;
            UpdateHomeEfforts();
        }
        catch { }
    }

    void UpdateHomeEfforts()
    {
        var modelRow = Selected("HomeModel");
        var effs = Items(modelRow, "efforts").Select(x => Convert.ToString(x)).Where(s => s.Length > 0).ToArray();
        if (effs.Length == 0) effs = new[] { "default" };
        var defEff = Str(modelRow, "default_effort");
        var prevEff = Str(Selected("HomeEffort"), "id");
        var rows = effs.Select(e => new Record
        {
            { "id", e },
            { "label", e == "xhigh" ? "XHigh" : e == "default" ? L("effort_default") : char.ToUpperInvariant(e[0]) + e.Substring(1) }
        }).ToArray();
        var pick = prevEff.Length > 0 && effs.Contains(prevEff) ? prevEff : (defEff.Length > 0 && effs.Contains(defEff) ? defEff : effs[0]);
        Fill("HomeEffort", rows, r => Str(r, "label"), pick);
    }

    void UpdateIdeRow(string prefix, Record info)
    {
        bool installed = Flag(info, "installed");
        string ver = Str(info, "version");
        string path = Str(info, "path");
        C<TextBlock>(prefix + "IdeStatus").Text = installed ? L("ide_installed") : L("ide_missing");
        C<TextBlock>(prefix + "IdeStatus").Foreground = installed ? mint : amber;
        C<Border>(prefix + "IdeBadge").Background = installed ? Brush("#0B2922") : Brush("#2E200B");
        C<Border>(prefix + "IdeBadge").BorderBrush = installed ? Brush("#195243") : Brush("#6E4E18");
        C<TextBlock>(prefix + "IdeDetail").Text = installed
            ? (ver.Length > 0 ? ver : path)
            : L("ide_not_detected_hint");
        C<Button>("Install" + prefix + "Btn").Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
    }

    async Task InstallIde(string provider)
    {
        await Api("ide_install", "provider", provider);
        await RefreshIdeStatus(true);
        Note(string.Format(L("ide_installed_fmt"), provider == "agy" ? "Antigravity CLI" : "ChatGPT Codex"));
    }

    Record BuildHomeStepFromInputs()
    {
        var prov = Str(Selected("HomeProvider"), "id");
        var proj = Str(Selected("HomeProject"), "id");
        var model = Str(Selected("HomeModel"), "id");
        var effort = Str(Selected("HomeEffort"), "id");
        var text = C<TextBox>("HomePromptText").Text.Trim();
        var modeItem = C<ComboBox>("HomeMode").SelectedItem as ComboBoxItem;
        var mode = modeItem != null && modeItem.Tag != null ? modeItem.Tag.ToString() : "read";
        if (prov.Length == 0) throw new InvalidOperationException(L("err_no_ide_installed"));
        if (proj.Length == 0) throw new InvalidOperationException(L("err_select_project"));
        if (text.Length == 0) throw new InvalidOperationException(L("err_enter_prompt"));
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var label = lines.Length > 0 ? lines[0].Trim() : "Prompt";
        if (label.Length > 48) label = label.Substring(0, 48);
        return new Record
        {
            { "provider", prov },
            { "project", proj },
            { "model", model },
            { "effort", effort },
            { "mode", mode },
            { "label", label },
            { "text", text }
        };
    }

    void StageHomeChainStep()
    {
        try
        {
            if (homeChainDraft.Count >= 9) { Note(L("err_chain_limit"), true); return; }
            var step = BuildHomeStepFromInputs();
            homeChainDraft.Add(step);
            C<TextBox>("HomePromptText").Clear();
            RefreshHomeChainBanner();
            Note(string.Format(L("chain_step_added"), homeChainDraft.Count));
        }
        catch (Exception err) { Note(err.Message, true); }
    }

    void RefreshHomeChainBanner()
    {
        var bar = C<Border>("HomeChainDraftBar");
        if (homeChainDraft.Count == 0)
        {
            bar.Visibility = Visibility.Collapsed;
            C<Button>("HomeSendBtn").Content = L("btn_send_prompt");
            return;
        }
        bar.Visibility = Visibility.Visible;
        var labels = string.Join(" → ", homeChainDraft.Select((s, idx) => (idx + 1) + ". " + Str(s, "label")).ToArray());
        C<TextBlock>("HomeChainSummary").Text = string.Format(L("chain_draft_fmt"), homeChainDraft.Count, labels);
        C<Button>("HomeSendBtn").Content = string.Format(L("btn_run_chain_fmt"), homeChainDraft.Count);
    }

    async Task SendHomePrompt()
    {
        bool offAfter = C<CheckBox>("HomeShutdownToggle").IsChecked == true;
        var curText = C<TextBox>("HomePromptText").Text.Trim();
        if (homeChainDraft.Count > 0)
        {
            var steps = new List<Record>(homeChainDraft);
            if (curText.Length > 0)
            {
                if (steps.Count >= 10) throw new InvalidOperationException(L("err_chain_limit"));
                steps.Add(BuildHomeStepFromInputs());
            }
            var res = await Api("chain_start", "steps", steps.ToArray(), "shutdown_after", offAfter);
            homeChainDraft.Clear();
            C<TextBox>("HomePromptText").Clear();
            RefreshHomeChainBanner();
            UpdatePowerUi(res);
            sessionSignature = "";
            Note(L("chain_started_note"));
            Navigate("Sessions");
            await RefreshState();
            return;
        }
        var step = BuildHomeStepFromInputs();
        await Api(
            "start_prompt",
            "provider", Str(step, "provider"),
            "project", Str(step, "project"),
            "model", Str(step, "model"),
            "effort", Str(step, "effort"),
            "text", Str(step, "text"),
            "mode", Str(step, "mode"),
            "shutdown_after", offAfter
        );
        C<TextBox>("HomePromptText").Clear();
        sessionSignature = "";
        Note(L("prompt_sent"));
        Navigate("Sessions");
        await RefreshState();
    }

    // ========================
    // Projects & Prompts + Drag-and-Drop Reorder (No Suggested Templates!)
    // ========================
    async void SetProjectFilter(string filter)
    {
        projectFilter = filter;
        UpdateFilterButtons();
        await Work(L("loading_projects"), () => ReadProjects(ProjectId()));
    }

    void UpdateFilterButtons()
    {
        SetPillState(C<Button>("FilterAll"), projectFilter == "");
        SetPillState(C<Button>("FilterAgy"), projectFilter == "agy");
        SetPillState(C<Button>("FilterCodex"), projectFilter == "codex");
    }

    void SetPillState(Button btn, bool active)
    {
        btn.Background = active ? Brush("#172446") : Brush("#131A2B");
        btn.BorderBrush = active ? Brush("#5B7CFA") : Brush("#263350");
        btn.Foreground = active ? textBrush : muted;
    }

    async Task ReadProjects(string selected)
    {
        var result = await Api("projects", "provider", projectFilter);
        var list = new List<Record>();
        list.Add(new Record
        {
            { "id", "*" },
            { "label", L("global_projects_title") },
            { "path", L("global_projects_sub") },
            { "is_global", true }
        });
        list.AddRange(Rows(result, "items"));
        FillChoices("ProjectList", list, row =>
        {
            if (Flag(row, "is_global"))
                return new Choice(row, Str(row, "label"), Str(row, "path"), "GLOBAL", Brush("#172446"), accent);
            var provs = Items(row, "providers").Select(p => Convert.ToString(p)).ToArray();
            string badge = Flag(row, "created_on_stick") ? "NEW" :
                (provs.Contains("codex") && provs.Contains("agy")) ? "BOTH" :
                provs.Contains("codex") ? "CODEX" :
                provs.Contains("agy") ? "AGY" : "";
            var bg = badge == "AGY" ? Brush("#10223D") : badge == "CODEX" ? Brush("#0B2922") :
                badge == "NEW" ? Brush("#2E200B") : Brush("#161F33");
            var fg = badge == "AGY" ? blue : badge == "CODEX" ? mint :
                badge == "NEW" ? amber : muted;
            return new Choice(row, Str(row, "label"), Str(row, "path"), badge, bg, fg);
        }, selected);
        if (C<ListBox>("ProjectList").SelectedIndex < 0 && C<ListBox>("ProjectList").Items.Count > 0)
            C<ListBox>("ProjectList").SelectedIndex = 0;

        var realProjects = Rows(result, "items");
        var prevHomeProj = Str(Selected("HomeProject"), "id");
        Fill("HomeProject", realProjects, row => Str(row, "label") + "  ·  " + Str(row, "path"), prevHomeProj.Length > 0 ? prevHomeProj : (realProjects.Length > 0 ? Str(realProjects[0], "id") : ""));
        if (C<ComboBox>("HomeProject").SelectedIndex < 0 && realProjects.Length > 0)
            C<ComboBox>("HomeProject").SelectedIndex = 0;
    }

    async Task ReadPrompts(string selected = "")
    {
        var project = Selected("ProjectList");
        var id = Str(project, "id");
        var isGlobal = id == "*";
        C<TextBlock>("ProjectHeading").Text = Display(project, "label", L("select_project"));
        C<TextBlock>("ProjectSubPath").Text = Str(project, "path");
        C<TextBlock>("GlobalPromptHint").Text = isGlobal ? L("global_hint") : "";
        C<Button>("RenameProjectBtn").IsEnabled = !isGlobal && id.Length > 0;
        C<Button>("ShowProject").IsEnabled = !isGlobal && id.Length > 0;
        C<Button>("BindProject").IsEnabled = !isGlobal && id.Length > 0;
        try
        {
            var result = id.Length == 0 ? new Record() : await Api("prompts", "project", id);
            if (id != ProjectId()) return;
            currentPromptRows = Rows(result, "items").ToList();
            FillPrompt(new Record());
            RenderPromptList(selected);
        }
        catch (Exception error) { Note(error.Message, true); }
    }

    void RenderPromptList(string selectedId)
    {
        int order = 1;
        FillChoices("PromptList", currentPromptRows, row =>
        {
            var p = Str(row, "provider");
            var tag = p == "codex" ? "Codex" : p == "agy" ? "AGY" : L("both_short");
            var idxLabel = "#" + (order++);
            return new Choice(
                row,
                Str(row, "label"),
                Str(row, "description"),
                tag,
                p == "codex" ? Brush("#0B2922") : p == "agy" ? Brush("#10223D") : Brush("#161F33"),
                p == "codex" ? mint : p == "agy" ? blue : muted,
                idxLabel
            );
        }, selectedId);
    }

    static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null)
        {
            var typed = current as T;
            if (typed != null) return typed;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    void WirePromptDragAndDrop()
    {
        var list = C<ListBox>("PromptList");
        list.PreviewMouseLeftButtonDown += (s, e) => { dragStartPoint = e.GetPosition(null); };
        list.PreviewMouseMove += (s, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var lbi = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (lbi == null) return;
            var choice = lbi.DataContext as Choice;
            if (choice == null) return;
            DragDrop.DoDragDrop(lbi, choice, DragDropEffects.Move);
        };
        list.DragOver += (s, e) =>
        {
            e.Effects = e.Data.GetDataPresent(typeof(Choice)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        list.Drop += async (s, e) =>
        {
            var sourceChoice = e.Data.GetData(typeof(Choice)) as Choice;
            if (sourceChoice == null) return;
            var targetLbi = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            var targetChoice = targetLbi != null ? targetLbi.DataContext as Choice : null;
            int oldIdx = currentPromptRows.FindIndex(r => Str(r, "id") == sourceChoice.Id);
            if (oldIdx < 0) return;
            int newIdx = targetChoice != null
                ? currentPromptRows.FindIndex(r => Str(r, "id") == targetChoice.Id)
                : currentPromptRows.Count - 1;
            if (newIdx < 0 || newIdx == oldIdx) return;
            var moved = currentPromptRows[oldIdx];
            currentPromptRows.RemoveAt(oldIdx);
            currentPromptRows.Insert(newIdx, moved);
            RenderPromptList(sourceChoice.Id);
            await PersistPromptOrder(sourceChoice.Id);
        };
    }

    async Task MoveSelectedPrompt(int delta)
    {
        var id = Str(Selected("PromptList"), "id");
        if (id.Length == 0) { Note(L("select_saved_prompt"), true); return; }
        int idx = currentPromptRows.FindIndex(r => Str(r, "id") == id);
        int target = idx + delta;
        if (idx < 0 || target < 0 || target >= currentPromptRows.Count) return;
        var moved = currentPromptRows[idx];
        currentPromptRows.RemoveAt(idx);
        currentPromptRows.Insert(target, moved);
        RenderPromptList(id);
        await PersistPromptOrder(id);
    }

    async Task PersistPromptOrder(string selectedId)
    {
        var projId = ProjectId();
        if (projId.Length == 0) return;
        var orderIds = currentPromptRows.Select(r => Str(r, "id")).ToArray();
        var res = await Api("reorder_prompts", "project", projId, "order", orderIds);
        currentPromptRows = Rows(res, "items").ToList();
        RenderPromptList(selectedId);
        Note(L("prompt_reordered"));
    }

    void FillPrompt(Record row)
    {
        loadedPrompt = row;
        C<TextBox>("PromptTitle").Text = Str(row, "label");
        C<TextBox>("PromptDescription").Text = Str(row, "description");
        C<TextBox>("PromptText").Text = Str(row, "text");
        C<ComboBox>("Provider").SelectedIndex = Str(row, "provider") == "codex" ? 1 : Str(row, "provider") == "agy" ? 2 : 0;
    }

    async Task SavePrompt()
    {
        var current = ProjectId();
        if (current.Length == 0) throw new InvalidOperationException(L("select_project_first"));
        var result = await Api("save_prompt", "project", current, "id", Str(loadedPrompt, "id"),
            "label", C<TextBox>("PromptTitle").Text,
            "description", C<TextBox>("PromptDescription").Text,
            "text", C<TextBox>("PromptText").Text,
            "provider", Scope());
        if (current == ProjectId()) await ReadPrompts(Str(Obj(result, "item"), "id"));
        Note(L("prompt_saved"));
    }

    async Task DeletePrompt()
    {
        var id = Str(loadedPrompt, "id");
        if (id.Length == 0) { Note(L("select_saved_prompt")); return; }
        if (MessageBox.Show(window, L("confirm_delete_prompt"), "AgentDeck",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Api("delete_prompt", "project", ProjectId(), "id", id);
        await ReadPrompts();
        Note(L("prompt_deleted"));
    }

    // ========================
    // Sleek Borderless Dark Modal Dialogs
    // ========================
    Window DarkModal(string title, double width, out StackPanel bodyHost)
    {
        var dlg = new Window
        {
            Owner = window,
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            Foreground = textBrush,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = 13.5
        };
        var frame = new Border
        {
            Background = Brush("#0F1422"),
            BorderBrush = Brush("#2E3E63"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(22),
            Margin = new Thickness(8)
        };
        var root = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var closeBtn = new Button
        {
            Content = "✕",
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            MinHeight = 28,
            Style = (Style)window.FindResource("ChromeBtn")
        };
        closeBtn.Click += (s, e) => dlg.DialogResult = false;
        DockPanel.SetDock(closeBtn, Dock.Right);
        var titleBlock = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = textBrush };
        header.MouseLeftButtonDown += (s, e) => { try { dlg.DragMove(); } catch { } };
        header.Children.Add(closeBtn);
        header.Children.Add(titleBlock);
        bodyHost = new StackPanel();
        root.Children.Add(header);
        root.Children.Add(bodyHost);
        frame.Child = root;
        dlg.Content = frame;
        return dlg;
    }

    async Task NewProject()
    {
        StackPanel panel;
        var dialog = DarkModal(L("new_project_title"), 450, out panel);
        var label = new TextBlock { Text = L("new_project_name_hint"), Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        var input = new TextBox { MaxLength = 64, Margin = new Thickness(0, 0, 0, 14) };
        var ideLabel = new TextBlock { Text = L("new_project_ide"), Foreground = muted, Margin = new Thickness(0, 0, 0, 6) };
        var ideBox = new ComboBox { Margin = new Thickness(0, 0, 0, 18) };
        ideBox.Items.Add(new ComboBoxItem { Content = L("both_ides"), Tag = "" });
        ideBox.Items.Add(new ComboBoxItem { Content = "Codex", Tag = "codex" });
        ideBox.Items.Add(new ComboBoxItem { Content = "Antigravity", Tag = "agy" });
        ideBox.SelectedIndex = projectFilter == "codex" ? 1 : projectFilter == "agy" ? 2 : 0;

        var rowBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = L("cancel"), Margin = new Thickness(0, 0, 8, 0) };
        var create = new Button { Content = L("create"), IsDefault = true, Style = (Style)window.FindResource("PrimaryBtn") };
        cancel.Click += (s, e) => dialog.DialogResult = false;
        create.Click += (s, e) => dialog.DialogResult = true;
        rowBtns.Children.Add(cancel);
        rowBtns.Children.Add(create);

        panel.Children.Add(label);
        panel.Children.Add(input);
        panel.Children.Add(ideLabel);
        panel.Children.Add(ideBox);
        panel.Children.Add(rowBtns);
        if (dialog.ShowDialog() != true) { Note(L("ready")); return; }

        var chosenProv = Convert.ToString(((ComboBoxItem)ideBox.SelectedItem).Tag);
        var res = await Api("create_project", "name", input.Text.Trim(), "provider", chosenProv);
        await ReadProjects(Str(res, "id"));
        Note(string.Format(L("project_created"), Str(res, "label")));
    }

    async Task RenameProject()
    {
        var row = Selected("ProjectList");
        var id = Str(row, "id");
        if (id.Length == 0 || id == "*") { Note(L("select_folder_rename"), true); return; }

        StackPanel panel;
        var dialog = DarkModal(L("rename_project_title"), 430, out panel);
        var label = new TextBlock { Text = L("rename_project_label"), Foreground = muted, Margin = new Thickness(0, 0, 0, 8) };
        var input = new TextBox { Text = Str(row, "label"), MaxLength = 64, Margin = new Thickness(0, 0, 0, 16) };
        input.SelectAll();
        var rowBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = L("cancel"), Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = L("save"), IsDefault = true, Style = (Style)window.FindResource("PrimaryBtn") };
        cancel.Click += (s, e) => dialog.DialogResult = false;
        save.Click += (s, e) => dialog.DialogResult = true;
        rowBtns.Children.Add(cancel);
        rowBtns.Children.Add(save);
        panel.Children.Add(label);
        panel.Children.Add(input);
        panel.Children.Add(rowBtns);
        if (dialog.ShowDialog() != true) { Note(L("ready")); return; }

        var res = await Api("rename_project", "project", id, "name", input.Text.Trim());
        await ReadProjects(Str(res, "id"));
        Note(string.Format(L("project_renamed"), Str(res, "label")));
    }

    async Task AddProject()
    {
        using (var picker = new Forms.FolderBrowserDialog { Description = L("select_project_folder") })
        {
            if (picker.ShowDialog() != Forms.DialogResult.OK) { Note(L("ready")); return; }
            var result = await Api("add_project", "path", picker.SelectedPath, "provider", projectFilter);
            await ReadProjects(Str(result, "id"));
            Note(L("folder_saved"));
        }
    }

    async Task BindProject()
    {
        var row = Selected("ProjectList");
        var id = Str(row, "id");
        if (row.Count == 0 || id == "*") throw new InvalidOperationException(L("select_project_first"));
        StackPanel content;
        var dialog = DarkModal(L("bind_agy_title"), 430, out content);
        var input = new TextBox { Text = Str(row, "agy_project"), MaxLength = 120, Margin = new Thickness(0, 8, 0, 16) };
        var rowBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = L("cancel"), Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = L("save"), IsDefault = true, Style = (Style)window.FindResource("PrimaryBtn") };
        cancel.Click += (s, e) => dialog.DialogResult = false;
        save.Click += (sender, e) => dialog.DialogResult = true;
        rowBtns.Children.Add(cancel);
        rowBtns.Children.Add(save);
        content.Children.Add(new TextBlock { Text = L("agy_project_id_label"), Foreground = muted });
        content.Children.Add(input);
        content.Children.Add(rowBtns);
        if (dialog.ShowDialog() != true) { Note(L("ready")); return; }
        await Api("add_project", "path", Str(row, "path"), "agy_project", input.Text);
        await ReadProjects(id);
        Note(L("binding_saved"));
    }

    async Task ShowProject()
    {
        var id = ProjectId();
        if (id == "*" || id.Length == 0) throw new InvalidOperationException(L("select_project_first"));
        var provider = Scope();
        await Api("show_project", "project", id, "device", DeviceId(),
            "provider", provider.Length > 0 ? provider : Display(Snapshot(), "provider", "codex"));
        Note(L("project_opened_stick"));
    }

    async void UseStickProject()
    {
        try
        {
            var target = Str(Snapshot(), "project");
            if (target.Length == 0) { Note(L("select_project_on_stick")); return; }
            if (target != ProjectId() && (C<TextBox>("PromptTitle").Text != Str(loadedPrompt, "label") ||
                C<TextBox>("PromptDescription").Text != Str(loadedPrompt, "description") ||
                C<TextBox>("PromptText").Text != Str(loadedPrompt, "text") || Scope() != Str(loadedPrompt, "provider")) &&
                MessageBox.Show(window, L("confirm_discard_prompt"), "AgentDeck",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            projectFilter = "";
            UpdateFilterButtons();
            Navigate("Prompts");
            if (target != ProjectId()) await Work(L("opening_stick_project"), () => ReadProjects(target));
            Note(L("project_synced"));
        }
        catch (Exception error) { Note(error.Message, true); }
    }

    // ========================
    // Sessions & Live Agent Activity Inspector
    // ========================
    void SwitchSessionTab(int tab)
    {
        sessionTab = tab;
        SetPillState(C<Button>("TabOutputBtn"), tab == 0);
        SetPillState(C<Button>("TabActivityBtn"), tab == 1);
        C<TextBox>("SessionAnswer").Visibility = tab == 0 ? Visibility.Visible : Visibility.Collapsed;
        C<ListBox>("ActivityFeedList").Visibility = tab == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task StopSelectedSession()
    {
        var id = Str(Selected("SessionList"), "id");
        if (id.Length == 0) return;
        await Api("stop_session", "job", id);
        sessionSignature = "";
        Note(L("session_stopping"));
        await RefreshState();
    }

    async Task RetrySelectedSession()
    {
        var id = Str(Selected("SessionList"), "id");
        if (id.Length == 0) return;
        await Api("retry_session", "job", id);
        sessionSignature = "";
        Note(L("session_retried"));
        await RefreshState();
    }

    async Task ChainNextToSession()
    {
        var id = Str(Selected("SessionList"), "id");
        if (id.Length == 0) return;
        StackPanel panel;
        var dialog = DarkModal(L("chain_next_title"), 480, out panel);
        var hint = new TextBlock { Text = L("chain_next_hint"), Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        var modeLbl = new TextBlock { Text = L("home_mode_label"), Foreground = muted, Margin = new Thickness(0, 0, 0, 5) };
        var modeBox = new ComboBox { Margin = new Thickness(0, 0, 0, 12) };
        modeBox.Items.Add(new ComboBoxItem { Content = L("home_mode_read"), Tag = "read" });
        modeBox.Items.Add(new ComboBoxItem { Content = L("home_mode_write"), Tag = "write" });
        modeBox.Items.Add(new ComboBoxItem { Content = L("home_mode_plan"), Tag = "plan_execute" });
        modeBox.SelectedIndex = 2;
        var promptLbl = new TextBlock { Text = L("home_prompt_label"), Foreground = muted, Margin = new Thickness(0, 0, 0, 5) };
        var input = new TextBox { Height = 120, MaxLength = 32000, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 12) };
        var offCheck = new CheckBox { Content = L("home_shutdown_label"), Foreground = textBrush, IsChecked = shutdownAfter || powerArmed, Margin = new Thickness(0, 0, 0, 16) };
        var rowBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = L("cancel"), Margin = new Thickness(0, 0, 8, 0) };
        var addBtn = new Button { Content = L("btn_chain_next_confirm"), IsDefault = true, Style = (Style)window.FindResource("PrimaryBtn") };
        cancel.Click += (s, e) => dialog.DialogResult = false;
        addBtn.Click += (s, e) => dialog.DialogResult = true;
        rowBtns.Children.Add(cancel);
        rowBtns.Children.Add(addBtn);
        panel.Children.Add(hint);
        panel.Children.Add(modeLbl);
        panel.Children.Add(modeBox);
        panel.Children.Add(promptLbl);
        panel.Children.Add(input);
        panel.Children.Add(offCheck);
        panel.Children.Add(rowBtns);
        if (dialog.ShowDialog() != true) { Note(L("ready")); return; }
        var text = input.Text.Trim();
        if (text.Length == 0) throw new InvalidOperationException(L("err_enter_prompt"));
        var chosenMode = Convert.ToString(((ComboBoxItem)modeBox.SelectedItem).Tag);
        var res = await Api("chain_append", "after_job", id, "text", text, "mode", chosenMode, "shutdown_after", offCheck.IsChecked == true);
        UpdatePowerUi(res);
        sessionSignature = "";
        Note(L("chain_appended_note"));
        await RefreshState();
    }

    async Task CancelSelectedChain()
    {
        var row = Selected("SessionList");
        var cid = Str(row, "chain_id");
        if (cid.Length == 0) return;
        await Api("chain_cancel", "chain_id", cid);
        sessionSignature = "";
        Note(L("chain_cancelled_note"));
        await RefreshState();
    }

    void RenderSessionQuestion(string jobId, Record result)
    {
        var panel = C<Border>("QuestionPanel");
        var optionsPanel = C<WrapPanel>("QuestionOptions");
        optionsPanel.Children.Clear();
        var q = Obj(result, "question");
        if (Str(result, "state") != "waiting_input" || q.Count == 0)
        {
            panel.Visibility = Visibility.Collapsed;
            return;
        }
        C<TextBlock>("QuestionHeader").Text = Display(q, "header", L("state_waiting_input"));
        C<TextBlock>("QuestionText").Text = Str(q, "question");
        var opts = Rows(q, "options");
        for (int i = 0; i < opts.Length; i++)
        {
            var idx = i;
            var opt = opts[i];
            var lbl = Str(opt, "label");
            var desc = Str(opt, "description");
            var btn = new Button
            {
                Content = lbl,
                ToolTip = desc.Length > 0 ? desc : null,
                Style = (Style)window.FindResource(i == 0 ? "PrimaryBtn" : "Button"),
                Margin = new Thickness(0, 0, 8, 6)
            };
            btn.Click += async (s, e) =>
            {
                panel.IsEnabled = false;
                await Work(L("sending_answer"), async () =>
                {
                    await Api("answer_question", "job", jobId, "choice", idx);
                    await ReadSession();
                    Note(L("answer_sent"));
                });
                panel.IsEnabled = true;
            };
            optionsPanel.Children.Add(btn);
        }
        panel.Visibility = Visibility.Visible;
    }

    string PhaseLabel(string phase)
    {
        if (string.IsNullOrEmpty(phase)) return "";
        string key = "phase_" + phase;
        string translated = L(key);
        return translated == key ? phase : translated;
    }

    void RenderActivityFeed(Record[] events)
    {
        C<Button>("TabActivityBtn").Content = string.Format(L("tab_activity_fmt"), events.Length);
        FillChoices("ActivityFeedList", events.Reverse(), ev =>
        {
            var ph = Str(ev, "phase");
            var st = Str(ev, "status");
            var ts = IntVal(ev, "ts", 0);
            var timeStr = ts > 0
                ? DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime.ToString("HH:mm:ss")
                : "";
            var badgeBg = st == "error" ? Brush("#2E131E") :
                ph == "command" ? Brush("#172446") :
                ph == "subagent" ? Brush("#241942") :
                ph == "browser" ? Brush("#0D283A") :
                ph == "file_edit" ? Brush("#0B2922") : Brush("#161F33");
            var badgeFg = st == "error" ? coral :
                ph == "command" ? accent :
                ph == "subagent" ? violet :
                ph == "browser" ? blue :
                ph == "file_edit" ? mint : muted;
            var tag = PhaseLabel(ph).ToUpperInvariant();
            if (tag.Length == 0) tag = "TOOL";
            return new Choice(ev, Str(ev, "title"), Str(ev, "detail"), tag, badgeBg, badgeFg, timeStr);
        }, "");
    }

    async Task ReadSession()
    {
        var id = Str(Selected("SessionList"), "id");
        C<Button>("OpenConversation").IsEnabled = false;
        C<Button>("OpenAnswer").IsEnabled = false;
        C<Button>("CopyOutputBtn").IsEnabled = false;
        C<Button>("StopSessionBtn").IsEnabled = false;
        C<Button>("RetrySessionBtn").IsEnabled = false;
        C<Button>("ChainNextBtn").IsEnabled = false;
        C<Button>("DeleteSessionBtn").IsEnabled = id.Length > 0;
        C<Button>("CancelChainBtn").Visibility = Visibility.Collapsed;
        C<Button>("OpenAnswer").Tag = null;
        C<Border>("QuestionPanel").Visibility = Visibility.Collapsed;
        C<Border>("SessionPhaseBadge").Visibility = Visibility.Collapsed;
        C<Border>("SessionChainBadge").Visibility = Visibility.Collapsed;
        if (id.Length == 0)
        {
            C<TextBlock>("SessionDetailTitle").Text = L("session_inspector");
            C<TextBox>("SessionAnswer").Clear();
            C<ListBox>("ActivityFeedList").ItemsSource = null;
            C<Button>("TabActivityBtn").Content = string.Format(L("tab_activity_fmt"), 0);
            C<TextBlock>("SessionLocation").Text = L("select_session_prompt");
            return;
        }
        try
        {
            var result = await Api("session_details", "job", id);
            if (id != Str(Selected("SessionList"), "id")) return;
            var provider = Str(result, "provider");
            var effort = Str(result, "effort");
            var state = Str(result, "state");
            var phase = Str(result, "phase");
            var chainId = Str(result, "chain_id");
            var cIdx = IntVal(result, "chain_index", 0);
            var cTot = IntVal(result, "chain_total", 0);
            var isActive = state == "queued" || state == "running" || state == "waiting_input";

            C<TextBlock>("SessionDetailTitle").Text = Display(result, "label", L("session_inspector"));
            if (phase.Length > 0 && isActive)
            {
                C<Border>("SessionPhaseBadge").Visibility = Visibility.Visible;
                C<TextBlock>("SessionPhaseText").Text = PhaseLabel(phase);
            }
            if (cTot > 1)
            {
                C<Border>("SessionChainBadge").Visibility = Visibility.Visible;
                C<TextBlock>("SessionChainText").Text = string.Format(L("session_chain_fmt"), cIdx, cTot);
            }
            if (chainId.Length > 0 && isActive) C<Button>("CancelChainBtn").Visibility = Visibility.Visible;

            var heading = string.Join("  ·  ", new[] {
                provider == "agy" ? "Antigravity" : "Codex",
                Str(result, "model_label"),
                effort == "xhigh" ? "XHigh" : effort,
                Str(result, "mode"),
                SessionState(state)
            }.Where(value => value.Length > 0));
            var tools = Items(result, "active_tools").Select(x => Convert.ToString(x)).Where(s => s.Length > 0).ToArray();
            if (tools.Length > 0 && isActive) heading += "\n" + string.Join("  |  ", tools);
            var title = Str(result, "conversation_title");
            if (provider == "agy" && Str(result, "conversation").Length > 0 && title.Length > 0)
                heading += "\nCtrl+K  ·  " + title;
            if (Flag(result, "answer_truncated")) heading += "\n" + L("full_answer_in_file");
            C<TextBlock>("SessionLocation").Text = heading;

            var body = Str(result, "answer");
            if (body.Length == 0) body = Str(result, "live_output");
            if (body.Length == 0) body = Display(result, "preview", Str(result, "detail"));
            C<TextBox>("SessionAnswer").Text = body;
            C<Button>("CopyOutputBtn").IsEnabled = body.Length > 0;
            RenderActivityFeed(Rows(result, "events"));
            RenderSessionQuestion(id, result);

            C<Button>("StopSessionBtn").IsEnabled = isActive;
            C<Button>("RetrySessionBtn").IsEnabled = !isActive && state != "stopping";
            C<Button>("ChainNextBtn").IsEnabled = isActive || state == "finished";
            C<Button>("OpenConversation").IsEnabled = Flag(result, "can_open_conversation");
            var path = Str(result, "output_path");
            C<Button>("OpenAnswer").Tag = path;
            C<Button>("OpenAnswer").IsEnabled = path.Length > 0;
        }
        catch (Exception error)
        {
            if (id == Str(Selected("SessionList"), "id")) C<TextBlock>("SessionLocation").Text = error.Message;
        }
    }

    // ========================
    // Accounts, Devices, Wi-Fi, Remote, Settings, Feedback, Updater, Onboarding
    // ========================
    async void OnCodexListButtonClick(object sender, RoutedEventArgs e)
    {
        var btn = e.OriginalSource as Button;
        if (btn == null) return;
        var kind = btn.Tag as string;
        var id = btn.CommandParameter as string;
        if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(id)) return;
        e.Handled = true;
        if (kind == "switch")
        {
            await Work(L("switching_account"), async () =>
            {
                var res = await Api("switch_account", "provider", "codex", "account", id);
                await RefreshAccounts(false);
                Note(Display(res, "message", L("account_switched")));
            });
        }
        else if (kind == "delete")
        {
            await Work(L("removing_account"), async () =>
            {
                await Api("delete_account", "provider", "codex", "account", id);
                await RefreshAccounts(false);
                Note(L("account_removed"));
            });
        }
    }

    async Task RefreshAccounts(bool force)
    {
        var res = await Api("accounts", "force", force);
        var codex = Obj(res, "codex");
        var cActive = Obj(codex, "active");
        var cEmail = Str(cActive, "email");
        var cPlan = Str(cActive, "plan");
        C<TextBlock>("CodexActiveText").Text = cEmail.Length > 0
            ? (cPlan.Length > 0 ? cEmail + " · " + cPlan.ToUpperInvariant() : cEmail)
            : L("not_signed_in");
        var cProfiles = Rows(codex, "profiles");
        if (knownCodexCount >= 0 && cProfiles.Length > knownCodexCount && DateTime.UtcNow < loginWatchUntil)
            loginWatchUntil = DateTime.MinValue;
        knownCodexCount = cProfiles.Length;
        FillChoices("CodexAccountList", cProfiles, row =>
        {
            var isAct = Flag(row, "active");
            var email = Str(row, "email");
            var plan = Str(row, "plan");
            var sub = string.Join(" · ", new[] { email, plan.Length > 0 ? plan.ToUpperInvariant() : "" }.Where(s => s.Length > 0));
            return new Choice(
                row,
                Str(row, "label"),
                sub,
                isAct ? L("active_badge") : "",
                Brush("#0B2922"),
                mint,
                isAct ? "" : L("btn_switch_inline")
            );
        }, Str(Selected("CodexAccountList"), "id"));

        var agy = Obj(res, "agy");
        var aActive = Obj(agy, "active");
        var aEmail = Str(aActive, "email");
        var aPlan = Str(aActive, "plan");
        var aQuota = Str(aActive, "quota_summary");
        var creditsObj = Get(aActive, "credits");
        var creditsStr = creditsObj == null ? "" : string.Format(L("credits_fmt"), Convert.ToString(creditsObj));

        C<TextBlock>("AgyActiveText").Text = aEmail.Length > 0 ? aEmail : L("not_signed_in");
        C<TextBlock>("AgyPlanText").Text = aPlan.Length > 0
            ? string.Format(L("agy_plan_fmt"), aPlan.ToUpperInvariant())
            : (aEmail.Length > 0 ? L("agy_plan_active") : L("not_signed_in"));
        C<TextBlock>("AgyCreditsText").Text = creditsStr;
        C<TextBlock>("AgyQuotaText").Text = aQuota.Length > 0 ? aQuota : L("agy_quota_empty");

        FillChoices("AgyAccountList", Rows(agy, "profiles"), row =>
        {
            var isAct = Flag(row, "active");
            return new Choice(row, Str(row, "label"), Str(row, "email"), isAct ? L("active_badge") : "", Brush("#10223D"), blue);
        }, "");
        Note(L("accounts_refreshed"));
    }

    async Task LoginCodexAccount()
    {
        await Api("login_account", "provider", "codex");
        loginWatchUntil = DateTime.UtcNow.AddSeconds(65);
        Note(L("login_launched"));
    }

    async Task RefreshRemoteControl()
    {
        var res = await Api("remote_control_status");
        ApplyRemoteStatus("codex", Obj(res, "codex"));
        ApplyRemoteStatus("agy", Obj(res, "agy"));
        var relay = await Api("relay_get");
        UpdateLocalRelayBadge(Obj(relay, "local_relay"));
    }

    async Task RemoteControlAction(string provider, string action)
    {
        var name = provider == "agy" ? C<TextBox>("AgyRcName").Text.Trim() : "";
        var res = await Api("remote_control_action", "provider", provider, "action", action, "name", name);
        var st = Obj(res, "status");
        ApplyRemoteStatus(provider, st);
        if (provider == "codex" && action == "pair")
        {
            var code = Str(st, "code");
            var url = Str(st, "url");
            var raw = Str(st, "raw");
            var outText = code.Length > 0 ? "Code: " + code + (url.Length > 0 ? "  |  " + url : "") : raw;
            if (outText.Length > 0)
            {
                C<TextBox>("CodexRcOutput").Text = outText;
                C<TextBox>("CodexRcOutput").Visibility = Visibility.Visible;
            }
        }
        Note(L("rc_updated"));
    }

    void ApplyRemoteStatus(string provider, Record st)
    {
        var running = Flag(st, "running");
        var err = Str(st, "error");
        if (provider == "codex")
        {
            C<TextBlock>("CodexRcStatus").Text = err.Length > 0 ? L("unavailable") : running ? L("rc_active") : L("rc_inactive");
            C<TextBlock>("CodexRcStatus").Foreground = running ? mint : err.Length > 0 ? coral : muted;
            var parts = new List<string>();
            if (Flag(st, "desktop_remote")) parts.Add(L("rc_desktop_connected"));
            if (Flag(st, "daemon_running")) parts.Add(L("rc_daemon_running"));
            var envId = Str(st, "environment_id");
            if (envId.Length > 0) parts.Add("Env: " + envId);
            if (err.Length > 0) parts.Add(err);
            C<TextBlock>("CodexRcDetail").Text = parts.Count > 0 ? string.Join("  ·  ", parts.ToArray()) : L("rc_codex_idle");
        }
        else
        {
            var reg = Flag(st, "registered");
            var ideServe = Flag(st, "ide_serve");
            var inst = Str(st, "instance");
            C<TextBlock>("AgyRcStatus").Text = err.Length > 0 ? L("unavailable") :
                ideServe ? L("rc_active_ide") : running ? L("rc_active") : reg ? L("rc_registered") : L("rc_not_registered");
            C<TextBlock>("AgyRcStatus").Foreground = (running || ideServe) ? blue : err.Length > 0 ? coral : muted;
            C<TextBlock>("AgyRcDetail").Text = err.Length > 0 ? err :
                (inst.Length > 0 ? L("rc_instance") + ": " + inst : Display(st, "raw", L("rc_agy_idle")));
        }
    }

    void UpdateLocalRelayBadge(Record status)
    {
        var running = Flag(status, "running");
        var port = Str(status, "port");
        C<TextBlock>("LocalRelayStatus").Text = running
            ? string.Format(L("local_relay_running"), port.Length > 0 ? port : "8793")
            : L("local_relay_stopped");
        C<TextBlock>("LocalRelayStatus").Foreground = running ? mint : muted;
    }

    async Task StartLocalRelay()
    {
        var status = await Api("local_relay_start", "bind", "127.0.0.1", "port", 8793);
        UpdateLocalRelayBadge(status);
        Note(L("local_relay_started"));
    }

    async Task StopLocalRelay()
    {
        var status = await Api("local_relay_stop");
        UpdateLocalRelayBadge(status);
        Note(L("local_relay_stopped"));
    }

    void UpdateAutostartUi(bool enabled, bool syncOnboarding = true)
    {
        suppressAutostartEvent = true;
        C<CheckBox>("AutostartToggle").IsChecked = enabled;
        if (syncOnboarding) C<CheckBox>("OnbAutoToggle").IsChecked = enabled;
        suppressAutostartEvent = false;
        C<TextBlock>("AutostartStateText").Text = enabled ? L("enabled") : L("disabled");
        C<TextBlock>("AutostartStateText").Foreground = enabled ? mint : muted;
    }

    async Task OnAutostartToggled(bool enabled)
    {
        if (suppressAutostartEvent) return;
        await Work(L("saving_settings"), async () =>
        {
            var res = await Api("autostart_set", "enabled", enabled);
            var actual = Flag(res, "autostart");
            UpdateAutostartUi(actual);
            Note(actual ? L("autostart_on") : L("autostart_off"));
        });
    }

    async Task ApplySelectedLanguage()
    {
        var item = (ComboBoxItem)C<ComboBox>("LanguagePicker").SelectedItem;
        var chosen = item != null ? Convert.ToString(item.Tag) : "en";
        var res = await Api("settings_save", "language", chosen);
        lang = Str(res, "language") == "pl" ? "pl" : "en";
        ApplyLanguage();
        await ReadProjects(ProjectId());
        Note(Flag(res, "stick_synced") ? L("lang_saved_synced") : L("lang_saved"));
    }

    void BrowseProjectsRoot()
    {
        using (var picker = new Forms.FolderBrowserDialog { Description = L("settings_projects_title") })
        {
            if (picker.ShowDialog() == Forms.DialogResult.OK)
                C<TextBox>("ProjectsRootInput").Text = picker.SelectedPath;
        }
    }

    async Task SaveProjectsRoot()
    {
        var raw = C<TextBox>("ProjectsRootInput").Text.Trim();
        var res = await Api("settings_save", "projects_root", raw);
        C<TextBox>("ProjectsRootInput").Text = Str(res, "projects_root");
        Note(L("settings_saved"));
    }

    void SetReportKind(string kind)
    {
        reportKind = kind;
        SetPillState(C<Button>("ReportCatBug"), kind == "bug");
        SetPillState(C<Button>("ReportCatFeature"), kind == "feature");
        SetPillState(C<Button>("ReportCatOther"), kind == "other");
    }

    async Task SubmitFeedbackReport()
    {
        var title = C<TextBox>("ReportTitle").Text.Trim();
        var body = C<TextBox>("ReportBody").Text.Trim();
        var contact = C<TextBox>("ReportContact").Text.Trim();
        var includeLogs = C<CheckBox>("ReportIncludeLogs").IsChecked == true;
        var res = await Api(
            "report_submit",
            "kind", reportKind,
            "title", title,
            "description", body,
            "contact", contact,
            "include_logs", includeLogs
        );
        var eventId = Str(res, "event_id");
        C<TextBox>("ReportTitle").Clear();
        C<TextBox>("ReportBody").Clear();
        var msg = string.Format(L("report_sent_fmt"), eventId.Length > 8 ? eventId.Substring(0, 8) : eventId);
        C<TextBlock>("ReportStatusText").Text = msg;
        C<TextBlock>("ReportStatusText").Foreground = mint;
        Note(msg);
    }

    async Task CheckForUpdateQuietly()
    {
        try
        {
            await Task.Delay(2500);
            await CheckForUpdateAsync(true);
        }
        catch { }
    }

    async Task CheckForUpdateAsync(bool silent)
    {
        var res = await Api("update_check");
        if (Flag(res, "available"))
        {
            updateVersion = Display(res, "latest_version", Str(res, "tag"));
            updateAssetUrl = Str(res, "asset_url");
            updateAssetName = Str(res, "asset_name");
            updateState = "available";
            ShowUpdateBanner();
            Note(string.Format(L("update_found_note"), updateVersion));
        }
        else if (!silent) Note(L("no_updates_found"));
    }

    void ShowUpdateBanner()
    {
        var banner = C<Border>("UpdateBanner");
        var wasHidden = banner.Visibility != Visibility.Visible;
        banner.Visibility = Visibility.Visible;
        RefreshUpdateBannerTexts();
        if (wasHidden) AnimateEntrance(banner);
    }

    void HideUpdateBanner()
    {
        if (updateState == "downloading") return;
        C<Border>("UpdateBanner").Visibility = Visibility.Collapsed;
    }

    void RefreshUpdateBannerTexts()
    {
        if (updateVersion.Length == 0) return;
        var btn = C<Button>("UpdateActionBtn");
        var bar = C<ProgressBar>("UpdateProgressBar");
        var dot = C<Ellipse>("UpdateDot");
        if (updateState == "downloading")
        {
            C<TextBlock>("UpdateBannerTitle").Text = string.Format(L("update_downloading_title"), updateVersion);
            btn.Content = L("btn_downloading");
            btn.IsEnabled = false;
            bar.Visibility = Visibility.Visible;
            bar.Foreground = accent;
            dot.Fill = accent;
        }
        else if (updateState == "ready")
        {
            C<TextBlock>("UpdateBannerTitle").Text = string.Format(L("update_ready_title"), updateVersion);
            C<TextBlock>("UpdateBannerSub").Text = L("update_ready_sub");
            btn.Content = L("btn_restart_update");
            btn.IsEnabled = true;
            btn.Background = mint;
            btn.BorderBrush = Brush("#6EE7B7");
            btn.Foreground = Brush("#042419");
            bar.Visibility = Visibility.Visible;
            bar.Value = 100;
            bar.Foreground = mint;
            dot.Fill = mint;
        }
        else
        {
            C<TextBlock>("UpdateBannerTitle").Text = string.Format(L("update_avail_title"), updateVersion);
            C<TextBlock>("UpdateBannerSub").Text = L("update_avail_sub");
            btn.Content = string.Format(L("btn_update_to"), updateVersion);
            btn.IsEnabled = true;
            btn.ClearValue(Control.BackgroundProperty);
            btn.ClearValue(Control.BorderBrushProperty);
            btn.ClearValue(Control.ForegroundProperty);
            bar.Visibility = Visibility.Collapsed;
            dot.Fill = accent;
        }
    }

    async Task HandleUpdateBannerClick()
    {
        if (updateState == "available")
        {
            await Work(L("downloading_update"), async () =>
            {
                await Api("update_download", "asset_url", updateAssetUrl, "asset_name", updateAssetName, "version", updateVersion);
                updateState = "downloading";
                C<ProgressBar>("UpdateProgressBar").Value = 0;
                RefreshUpdateBannerTexts();
            });
        }
        else if (updateState == "ready")
        {
            await Work(L("applying_update"), async () =>
            {
                await Api("update_apply");
                window.Close();
            });
        }
    }

    async Task PollUpdateProgress()
    {
        if (updateState != "downloading") return;
        var st = await Api("update_status");
        var status = Str(st, "status");
        var pct = IntVal(st, "percent", 0);
        var dlMb = (Convert.ToDouble(Get(st, "downloaded") ?? 0)) / (1024.0 * 1024.0);
        var totMb = (Convert.ToDouble(Get(st, "total") ?? 0)) / (1024.0 * 1024.0);
        if (status == "downloading")
        {
            C<ProgressBar>("UpdateProgressBar").IsIndeterminate = totMb <= 0;
            if (totMb > 0)
            {
                C<ProgressBar>("UpdateProgressBar").Value = pct;
                C<TextBlock>("UpdateBannerSub").Text = string.Format(L("update_dl_pct"), pct, dlMb, totMb);
            }
            else C<TextBlock>("UpdateBannerSub").Text = string.Format(L("update_dl_mb"), dlMb);
        }
        else if (status == "ready")
        {
            C<ProgressBar>("UpdateProgressBar").IsIndeterminate = false;
            updateState = "ready";
            ShowUpdateBanner();
            Note(string.Format(L("update_ready_title"), updateVersion));
        }
        else if (status == "error")
        {
            updateState = "available";
            ShowUpdateBanner();
            Note(Str(st, "error"), true);
        }
    }

    void ShowOnboarding(int step)
    {
        onboardingStep = Math.Max(1, Math.Min(3, step));
        var overlay = C<Grid>("OnboardingOverlay");
        var wasHidden = overlay.Visibility != Visibility.Visible;
        overlay.Visibility = Visibility.Visible;
        if (wasHidden) AnimateEntrance(C<FrameworkElement>("OnboardingCard"));
        else AnimateEntrance(C<FrameworkElement>("OnbStep" + onboardingStep));

        C<StackPanel>("OnbStep1").Visibility = onboardingStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        C<StackPanel>("OnbStep2").Visibility = onboardingStep == 2 ? Visibility.Visible : Visibility.Collapsed;
        C<StackPanel>("OnbStep3").Visibility = onboardingStep == 3 ? Visibility.Visible : Visibility.Collapsed;
        C<Button>("OnbBackBtn").Visibility = onboardingStep > 1 ? Visibility.Visible : Visibility.Collapsed;

        UpdateOnboardingLanguageCards();
        UpdateOnboardingTexts();
        if (onboardingStep == 3) UpdateOnboardingStick(lastDevicesResult);
    }

    void UpdateOnboardingLanguageCards()
    {
        var enBtn = C<Button>("OnbLangEnBtn");
        var plBtn = C<Button>("OnbLangPlBtn");
        enBtn.Background = lang == "en" ? Brush("#172446") : Brush("#080B13");
        enBtn.BorderBrush = lang == "en" ? accent : Brush("#26334E");
        plBtn.Background = lang == "pl" ? Brush("#172446") : Brush("#080B13");
        plBtn.BorderBrush = lang == "pl" ? accent : Brush("#26334E");
    }

    void UpdateOnboardingTexts()
    {
        C<TextBlock>("OnbTagline").Text = L("tagline");
        C<TextBlock>("OnbStepBadge").Text = string.Format(L("onb_step_badge"), onboardingStep);
        C<TextBlock>("OnbStepTitle").Text = L("onb_step" + onboardingStep + "_title");
        C<TextBlock>("OnbStepSub").Text = L("onb_step" + onboardingStep + "_sub");
        C<TextBlock>("OnbLangPrompt").Text = L("onb_lang_prompt");
        C<TextBlock>("OnbLangEnSub").Text = L("onb_lang_en_sub");
        C<TextBlock>("OnbLangPlSub").Text = L("onb_lang_pl_sub");
        C<TextBlock>("OnbAutoTitle").Text = L("onb_auto_title");
        C<TextBlock>("OnbAutoDesc").Text = L("onb_auto_desc");
        C<TextBlock>("OnbAutoHint").Text = L("onb_auto_hint");
        C<Button>("OnbPairUsbBtn").Content = L("btn_pair_usb");
        C<Button>("OnbSkipBtn").Content = L("onb_skip");
        C<Button>("OnbBackBtn").Content = L("onb_back");
        C<Button>("OnbNextBtn").Content = onboardingStep == 3 ? L("onb_finish") : L("onb_continue");
    }

    async Task PickOnboardingLanguage(string chosen)
    {
        lang = chosen == "pl" ? "pl" : "en";
        C<ComboBox>("LanguagePicker").SelectedIndex = lang == "pl" ? 1 : 0;
        ApplyLanguage();
        UpdateOnboardingLanguageCards();
        UpdateOnboardingTexts();
        try
        {
            await Api("settings_save", "language", lang);
            await ReadProjects(ProjectId());
        }
        catch { }
    }

    async Task AdvanceOnboarding()
    {
        if (onboardingStep == 1) { ShowOnboarding(2); return; }
        if (onboardingStep == 2)
        {
            bool autoOn = C<CheckBox>("OnbAutoToggle").IsChecked == true;
            await Work(L("saving_settings"), async () =>
            {
                var res = await Api("settings_save", "language", lang, "autostart", autoOn);
                UpdateAutostartUi(Flag(res, "autostart"));
            });
            ShowOnboarding(3);
            return;
        }
        await FinishOnboarding();
    }

    async Task OnboardingPairUsb()
    {
        await Api("pair_usb", "address", "", "code", "");
        Note(L("stick_paired"));
        await RefreshState();
        UpdateOnboardingStick(lastDevicesResult);
    }

    void ShowAfterUpdate()
    {
        var overlay = C<Grid>("AfterUpdateOverlay");
        overlay.Visibility = Visibility.Visible;
        AnimateEntrance(C<FrameworkElement>("AfterUpdateCard"));
    }

    async Task CloseAfterUpdate()
    {
        C<Grid>("AfterUpdateOverlay").Visibility = Visibility.Collapsed;
        if (appVersion.Length > 0)
        {
            try { await Api("settings_save", "last_seen_version", appVersion); } catch { }
        }
    }

    async Task FlashStickFirmware()
    {
        var res = await Api("stick_flash_firmware");
        stickFwDismissed = true;
        C<Border>("StickFwBanner").Visibility = Visibility.Collapsed;
        var fw = Str(res, "firmware");
        if (fw.Length == 0) fw = "2.0.0";
        Note(string.Format(L("stick_fw_flashed_fmt"), fw));
        await RefreshState();
    }

    async Task DeleteSelectedSession()
    {
        var id = Str(Selected("SessionList"), "id");
        if (id.Length == 0) return;
        await Api("delete_session", "job", id);
        sessionSignature = "";
        C<TextBox>("SessionAnswer").Clear();
        C<ListBox>("ActivityFeedList").ItemsSource = null;
        C<TextBlock>("SessionLocation").Text = L("select_session_prompt");
        C<Button>("DeleteSessionBtn").IsEnabled = false;
        Note(L("session_deleted"));
        await RefreshState();
    }

    async Task ClearFinishedSessions()
    {
        var res = await Api("clear_sessions");
        sessionSignature = "";
        C<TextBox>("SessionAnswer").Clear();
        C<ListBox>("ActivityFeedList").ItemsSource = null;
        C<TextBlock>("SessionLocation").Text = L("select_session_prompt");
        Note(string.Format(L("sessions_cleared_fmt"), IntVal(res, "deleted")));
        await RefreshState();
    }

    async Task FinishOnboarding()
    {
        await Work(L("saving_settings"), async () =>
        {
            bool autoOn = C<CheckBox>("OnbAutoToggle").IsChecked == true;
            var res = await Api("settings_save", "language", lang, "autostart", autoOn, "onboarded", true, "last_seen_version", appVersion);
            UpdateAutostartUi(Flag(res, "autostart"));
        });
        C<Grid>("OnboardingOverlay").Visibility = Visibility.Collapsed;
        Note(L("ready"));
    }

    void UpdateOnboardingStick(Record result)
    {
        if (C<Grid>("OnboardingOverlay").Visibility != Visibility.Visible || onboardingStep != 3) return;
        var usb = Flag(result, "usb");
        var rows = Rows(result, "devices");
        var active = rows.FirstOrDefault(r => Flag(r, "usb")) ?? rows.FirstOrDefault(r => Flag(r, "online")) ?? new Record();
        var paired = Flag(active, "paired");
        var snap = Obj(active, "snapshot");
        var bat = IntVal(snap, "battery");
        var chg = Flag(snap, "charging");
        var batPart = bat >= 0 ? (" · " + (chg ? "⚡ " : "") + bat + "%") : "";

        C<Ellipse>("OnbStickDot").Fill = usb ? mint : rows.Any(r => Flag(r, "online")) ? blue : amber;
        C<TextBlock>("OnbStickState").Text = usb
            ? (paired ? L("onb_stick_usb_paired") + batPart : L("onb_stick_usb_unpaired") + batPart)
            : (paired ? L("onb_stick_wifi") + batPart : L("onb_stick_missing"));
        C<TextBlock>("OnbStickDetail").Text = usb
            ? (paired ? L("onb_stick_ready_desc") : L("onb_stick_pair_desc"))
            : L("onb_stick_wait_desc");
        C<Button>("OnbPairUsbBtn").IsEnabled = usb;
        C<Button>("OnbPairUsbBtn").Visibility = usb && paired ? Visibility.Collapsed : Visibility.Visible;
    }

    async Task Pair(string method, bool useCode)
    {
        var code = useCode ? C<TextBox>("PairCode").Text.Trim() : "";
        if (useCode && code.Length == 0) throw new InvalidOperationException(L("enter_stick_code"));
        await Api(method, "address", C<TextBox>("DeviceAddress").Text.Trim(), "code", code);
        C<TextBox>("PairCode").Clear();
        Note(L("stick_paired"));
        await RefreshState();
    }

    string DeviceTitle(Record row)
    {
        var id = Str(row, "id");
        var status = Flag(row, "usb") ? "USB" : Flag(row, "remote") ? L("internet") :
            Flag(row, "online") ? "Wi-Fi" : Flag(row, "pairing") ? L("code_active") : "Offline";
        var address = Str(row, "address");
        return "StickS3 " + id.Substring(Math.Max(0, id.Length - 4)).ToUpperInvariant() + "  ·  " + status +
            (address.Length > 0 ? "  ·  " + address : "");
    }

    string BatteryBadgeText(Record snap)
    {
        var bat = IntVal(snap, "battery");
        if (bat < 0) return "";
        return (Flag(snap, "charging") ? "⚡ " : "") + bat + "%";
    }

    async Task Discover()
    {
        var result = await Api("discover", "address", C<TextBox>("DeviceAddress").Text.Trim());
        var found = Rows(result, "devices");
        if (found.Length > 0)
        {
            var ready = found.FirstOrDefault(row => Flag(row, "pairing")) ?? found[0];
            FillChoices("DeviceList", found, row => new Choice(row, DeviceTitle(row), Str(row, "address"), Flag(row, "pairing") ? L("code_active") : "", Brush("#172446"), accent), Str(ready, "id"));
            Note(L("select_stick_enter_code"));
        }
        else Note(L("no_sticks_found"));
    }

    async Task Forget()
    {
        var row = Selected("DeviceList");
        if (row.Count == 0) throw new InvalidOperationException(L("select_paired_stick"));
        await Api("forget", "device", Str(row, "id"));
        deviceSignature = "";
        await RefreshState();
        Note(L("pairing_removed"));
    }

    void UpdateSidebarBattery(Record snapshot)
    {
        var bat = IntVal(snapshot, "battery");
        var charging = Flag(snapshot, "charging");
        var badge = C<Border>("SidebarBatteryBadge");
        var bar = C<ProgressBar>("SidebarBatteryBar");
        if (bat < 0)
        {
            badge.Visibility = Visibility.Collapsed;
            bar.Visibility = Visibility.Collapsed;
            return;
        }
        badge.Visibility = Visibility.Visible;
        bar.Visibility = Visibility.Visible;
        bar.Value = bat;
        C<TextBlock>("SidebarBatteryText").Text = (charging ? "⚡ " : "") + bat + "%";
        SolidColorBrush color = (charging || bat >= 45) ? mint : (bat >= 20 ? amber : coral);
        SolidColorBrush bg = (charging || bat >= 45) ? Brush("#0B2922") : (bat >= 20 ? Brush("#2E200B") : Brush("#2E131E"));
        SolidColorBrush bd = (charging || bat >= 45) ? Brush("#195243") : (bat >= 20 ? Brush("#5C4319") : Brush("#5C2032"));
        C<TextBlock>("SidebarBatteryText").Foreground = color;
        bar.Foreground = color;
        badge.Background = bg;
        badge.BorderBrush = bd;
    }

    void UpdateHomeStickCard(Record result, Record snapshot, bool isConnected)
    {
        var usb = Flag(result, "usb");
        var bat = IntVal(snapshot, "battery");
        var chg = Flag(snapshot, "charging");
        var fw = Str(result, "stick_firmware");
        var batStr = bat >= 0 ? (" · " + (chg ? "⚡ " : "") + bat + "%") : "";
        var fwStr = fw.Length > 0 ? (" · v" + fw) : "";
        C<Ellipse>("HomeStickDot").Fill = usb ? mint : isConnected ? blue : amber;
        C<TextBlock>("HomeStickStatus").Text = (usb ? L("stick_usb") : isConnected ? L("stick_wifi") : L("waiting_stick")) + batStr + fwStr;
        C<TextBlock>("HomeStickDetail").Text = usb ? L("home_stick_usb_ok") : L("home_stick_usb_wait");
        C<Button>("HomePairUsbBtn").IsEnabled = usb;
    }

    void UpdateStickFirmwareBanner(Record result)
    {
        var banner = window.FindName("StickFwBanner") as Border;
        if (banner == null) return;
        bool needed = Flag(result, "firmware_update_needed");
        if (!needed) stickFwDismissed = false;
        if (!needed || stickFwDismissed)
        {
            banner.Visibility = Visibility.Collapsed;
            return;
        }
        bool usb = Flag(result, "usb");
        string stickVer = Str(result, "stick_firmware");
        string expVer = Str(result, "expected_firmware");
        if (expVer.Length == 0) expVer = "2.0.0";
        C<TextBlock>("StickFwTitle").Text = stickVer.Length > 0
            ? string.Format(L("stick_fw_title_ver"), stickVer, expVer)
            : string.Format(L("stick_fw_title"), expVer);
        C<TextBlock>("StickFwSub").Text = usb
            ? string.Format(L("stick_fw_ready_sub"), expVer)
            : string.Format(L("stick_fw_usb_sub"), expVer);
        C<Button>("StickFwUpdateBtn").Content = L("stick_fw_btn");
        C<Button>("StickFwUpdateBtn").IsEnabled = usb;
        C<Ellipse>("StickFwDot").Fill = usb ? mint : amber;
        banner.Visibility = Visibility.Visible;
    }

    async Task RefreshState()
    {
        if (ticking) return;
        ticking = true;
        try
        {
            var result = await Api("devices");
            lastDevicesResult = result;
            var ov = Obj(result, "overview");
            if (ov.Count > 0) UpdatePowerUi(ov);

            var serverLang = Str(result, "lang");
            if ((serverLang == "en" || serverLang == "pl") && serverLang != lang)
            {
                lang = serverLang;
                C<ComboBox>("LanguagePicker").SelectedIndex = lang == "pl" ? 1 : 0;
                ApplyLanguage();
            }
            var rows = Rows(result, "devices");
            var signature = string.Join("|", rows.Select(row => Str(row, "id") + DeviceTitle(row) + BatteryBadgeText(Obj(row, "snapshot"))));
            var device = DeviceId();
            if (deviceSignature != signature)
            {
                deviceSignature = signature;
                FillChoices("DeviceList", rows, row =>
                {
                    var snap = Obj(row, "snapshot");
                    var bat = IntVal(snap, "battery");
                    var batText = BatteryBadgeText(snap);
                    var sub = Str(snap, "screen").Length > 0 ? L("stick_screen") + ": " + Str(snap, "screen") : Str(row, "address");
                    var fg = bat >= 45 || Flag(snap, "charging") ? mint : bat >= 20 ? amber : coral;
                    var bg = bat >= 45 || Flag(snap, "charging") ? Brush("#0B2922") : bat >= 20 ? Brush("#2E200B") : Brush("#2E131E");
                    return new Choice(row, DeviceTitle(row), sub, batText, bg, fg);
                }, Str(Selected("DeviceList"), "id"));
                var activeDev = rows.FirstOrDefault(row => Str(row, "id") == device) ??
                                rows.FirstOrDefault(row => Flag(row, "usb")) ?? rows.FirstOrDefault();
                Fill("WifiDevice", rows, DeviceTitle, activeDev == null ? "" : Str(activeDev, "id"));
            }
            var selected = rows.FirstOrDefault(row => Str(row, "id") == DeviceId())
                           ?? rows.FirstOrDefault(row => Flag(row, "usb") || Flag(row, "online") || Flag(row, "remote"))
                           ?? rows.FirstOrDefault()
                           ?? new Record();
            var snapshot = Obj(selected, "snapshot");
            linkSnapshot = snapshot;
            C<TextBlock>("LinkStatus").Text = Flag(result, "usb") ? L("stick_usb") :
                rows.Any(row => Flag(row, "remote")) ? L("stick_remote") :
                rows.Any(row => Flag(row, "online")) ? L("stick_wifi") : L("bridge_ready");
            var isConnected = Flag(result, "usb") || rows.Any(row => Flag(row, "online") || Flag(row, "remote"));
            C<Ellipse>("LinkDot").Fill = isConnected ? mint : muted;
            C<TextBlock>("LinkStatus").Foreground = isConnected ? mint : muted;
            UpdateSidebarBattery(snapshot);
            UpdateStickFirmwareBanner(result);
            UpdateHomeStickCard(result, snapshot, isConnected);
            C<TextBlock>("Context").Text = Display(snapshot, "project_name", Display(snapshot, "session", Display(snapshot, "screen", L("waiting_stick"))));
            C<TextBlock>("WifiStatus").Text = Display(snapshot, "ssid", L("stick_no_wifi")) + "  " + Str(snapshot, "wifi_message");
            UpdateOnboardingStick(result);
            await UpdateWifi(snapshot);
            await PollUpdateProgress();

            if (DateTime.UtcNow < loginWatchUntil)
            {
                try { await RefreshAccounts(false); } catch { }
            }

            if (section == "Home" && currentHomeModels.Count == 0 && Str(Selected("HomeProvider"), "id").Length > 0)
            {
                await LoadHomeModels();
            }

            if (section == "Sessions")
            {
                var sesRes = await Api("recent_sessions");
                UpdatePowerUi(sesRes);
                var sessions = Rows(sesRes, "items");
                var states = string.Join("|", sessions.Select(row => Str(row, "id") + Str(row, "state") + Str(row, "phase") + Str(row, "chain_index")));
                if (states != sessionSignature)
                {
                    sessionSignature = states;
                    var selectedId = Str(Selected("SessionList"), "id");
                    FillChoices("SessionList", sessions, row =>
                    {
                        var st = Str(row, "state");
                        var prov = Str(row, "provider") == "agy" ? "AGY" : "Codex";
                        var cIdx = IntVal(row, "chain_index", 0);
                        var cTot = IntVal(row, "chain_total", 0);
                        var sub = prov + " · " + Str(row, "detail") + (cTot > 1 ? (" · " + cIdx + "/" + cTot) : "");
                        var fg = st == "finished" ? mint : st == "running" ? blue : (st == "error" || st == "orphaned") ? coral : amber;
                        var bg = st == "finished" ? Brush("#0B2922") : st == "running" ? Brush("#10223D") : (st == "error" || st == "orphaned") ? Brush("#2E131E") : Brush("#2E200B");
                        return new Choice(row, Str(row, "label"), sub, SessionState(st), bg, fg);
                    }, selectedId.Length > 0 ? selectedId : (sessions.Length > 0 ? Str(sessions[0], "id") : ""));
                    await ReadSession();
                }
                else
                {
                    var activeId = Str(Selected("SessionList"), "id");
                    if (activeId.Length > 0 && sessions.Any(s => Str(s, "id") == activeId &&
                        (Str(s, "state") == "running" || Str(s, "state") == "waiting_input" || Str(s, "state") == "queued")))
                    {
                        await ReadSession();
                    }
                }
            }
        }
        catch (Exception) { C<TextBlock>("LinkStatus").Text = L("bridge_offline"); }
        finally { ticking = false; }
    }

    string SessionState(string state)
    {
        switch (state)
        {
            case "queued": return L("state_queued");
            case "running": return L("state_running");
            case "waiting_input": return L("state_waiting_input");
            case "finished": return L("state_finished");
            case "cancelled": return L("state_cancelled");
            case "limit": return L("state_limit");
            case "error": return L("state_error");
            default: return state;
        }
    }

    async Task ScanWifi()
    {
        var device = DeviceId();
        if (device.Length == 0) throw new InvalidOperationException(L("connect_stick_first"));
        var result = await Api("wifi_scan", "device", device);
        if (device != DeviceId()) return;
        pendingScanDevice = device;
        pendingRevision = Convert.ToInt32(Get(result, "target_revision"));
        scanUntil = DateTime.UtcNow.AddSeconds(16);
    }

    async Task JoinWifi()
    {
        var network = Selected("NetworkList");
        if (network.Count == 0) throw new InvalidOperationException(L("select_network_err"));
        var box = C<PasswordBox>("WifiPassword");
        var secret = Flag(network, "secured") ? box.Password : "";
        var length = Encoding.UTF8.GetByteCount(secret);
        if (Flag(network, "secured") && (length < 8 || length > 63))
            throw new InvalidOperationException(L("password_length_err"));
        var device = DeviceId();
        box.Clear();
        var result = await Api("wifi_connect", "device", device, "ssid", Str(network, "ssid"), "password", secret);
        if (device != DeviceId()) return;
        pendingWifiDevice = device;
        pendingWifiId = Str(result, "wifi_action");
        wifiUntil = DateTime.UtcNow.AddSeconds(32);
        Note(L("stick_joining_wifi"));
    }

    async Task UpdateWifi(Record snapshot)
    {
        if (pendingScanDevice != null && pendingScanDevice == DeviceId())
        {
            if (Convert.ToInt32(Get(snapshot, "scan_revision")) >= pendingRevision)
            {
                var device = pendingScanDevice;
                pendingScanDevice = null;
                await Work(L("reading_networks"), async () =>
                {
                    var networks = Rows(await Api("wifi_list", "device", device), "networks");
                    if (device != DeviceId()) return;
                    Fill("NetworkList", networks, row => Str(row, "ssid") + "  ·  " +
                        (Flag(row, "secured") ? L("secured") : L("open")) + "  ·  " + Str(row, "rssi") + " dBm");
                    Note(networks.Length > 0 ? L("select_network") : L("no_networks_found"));
                });
            }
            else if (DateTime.UtcNow > scanUntil) { pendingScanDevice = null; Note(L("scan_timeout"), true); }
        }
        if (pendingWifiDevice != null && pendingWifiDevice == DeviceId())
        {
            var phase = Str(snapshot, "wifi_phase");
            if (Str(snapshot, "wifi_action") == pendingWifiId && (phase == "connected" || phase == "failed" || phase == "unsaved"))
            {
                pendingWifiDevice = null;
                Note(phase == "connected" ? L("wifi_connected") : Str(snapshot, "wifi_message"), phase != "connected");
            }
            else if (DateTime.UtcNow > wifiUntil) { pendingWifiDevice = null; Note(L("check_stick_screen")); }
        }
    }

    async Task SaveRelay(string endpoint)
    {
        var result = await Api("relay_save", "endpoint", endpoint);
        C<TextBox>("RelayAddress").Text = Str(result, "endpoint");
        Note(L("relay_saved"));
    }

    async Task ExportChannels()
    {
        var channels = Rows(await Api("relay_get"), "channels");
        if (channels.Length == 0) throw new InvalidOperationException(L("pair_for_channels"));
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "agentdeck-channels.json", Filter = "JSON|*.json" };
        if (dialog.ShowDialog(window) != true) { Note(L("ready")); return; }
        File.WriteAllText(dialog.FileName, new JavaScriptSerializer().Serialize(channels));
        Note(L("channels_saved"));
    }

    async Task TestRelay()
    {
        var device = DeviceId();
        await Api("relay_test", "device", device);
        bool reachable = false;
        Task cleanup;
        try
        {
            for (int i = 0; i < 12; ++i)
            {
                await Task.Delay(1000);
                var rows = Rows(await Api("devices"), "devices");
                if (rows.Any(row => Str(row, "id") == device && Flag(row, "remote")))
                {
                    reachable = true;
                    break;
                }
            }
        }
        finally { cleanup = Api("relay_test", "device", device, "enabled", false); }
        await cleanup;
        Note(reachable ? L("relay_reachable") : L("relay_unreachable"), !reachable);
    }

    void ApplyLanguage()
    {
        window.Title = L("window_title");
        C<TextBlock>("BrandTagline").Text = L("tagline");
        C<TextBlock>("NavHomeText").Text = L("nav_home");
        C<TextBlock>("NavPromptsText").Text = L("nav_prompts");
        C<TextBlock>("NavSessionsText").Text = L("nav_sessions");
        C<TextBlock>("NavAccountsText").Text = L("nav_accounts");
        C<TextBlock>("NavDevicesText").Text = L("nav_devices");
        C<TextBlock>("NavWifiText").Text = L("nav_wifi");
        C<TextBlock>("NavRemoteText").Text = L("nav_remote");
        C<TextBlock>("NavFeedbackText").Text = L("nav_feedback");
        C<TextBlock>("NavSettingsText").Text = L("nav_settings");

        C<TextBlock>("HomeQuickTitle").Text = L("home_quick_title");
        C<TextBlock>("HomeQuickDesc").Text = L("home_quick_desc");
        C<TextBlock>("HomeIdeLabel").Text = L("home_ide_label");
        C<TextBlock>("HomeModelLabel").Text = L("home_model_label");
        C<TextBlock>("HomeEffortLabel").Text = L("home_effort_label");
        C<TextBlock>("HomeModeLabel").Text = L("home_mode_label");
        C<ComboBoxItem>("HomeModeRead").Content = L("home_mode_read");
        C<ComboBoxItem>("HomeModeWrite").Content = L("home_mode_write");
        C<ComboBoxItem>("HomeModePlan").Content = L("home_mode_plan");
        C<TextBlock>("HomeProjectLabel").Text = L("home_project_label");
        C<TextBlock>("HomePromptLabel").Text = L("home_prompt_label");
        C<Button>("HomeStageChainBtn").Content = L("btn_add_chain_step");
        C<Button>("HomeClearChainBtn").Content = L("btn_clear");
        C<Button>("HomeOpenSessionsBtn").Content = L("btn_open_sessions");
        C<TextBlock>("HomeShutdownLabel").Text = L("home_shutdown_label");
        RefreshHomeChainBanner();

        C<TextBlock>("HomeIdesTitle").Text = L("home_ides_title");
        C<TextBlock>("HomeIdesDesc").Text = L("home_ides_desc");
        C<Button>("RefreshIdeBtn").Content = L("btn_refresh");
        C<Button>("InstallAgyBtn").Content = L("btn_install");
        C<Button>("InstallCodexBtn").Content = L("btn_install");
        C<TextBlock>("HomeStickTitle").Text = L("home_stick_title");
        C<Button>("HomePairUsbBtn").Content = L("btn_pair_usb");
        C<Button>("HomeFlashUsbBtn").Content = L("btn_flash_stick");
        C<TextBlock>("HomeLiveTitle").Text = L("home_live_title");
        C<Button>("HomeGoSessionsBtn").Content = L("btn_inspect");

        C<TextBlock>("ProjectsTitle").Text = L("projects_title");
        C<Button>("FilterAll").Content = L("filter_all");
        C<Button>("NewProjectBtn").Content = L("btn_new_project");
        C<Button>("RenameProjectBtn").Content = L("btn_rename");
        C<Button>("AddProject").Content = L("btn_add_folder");
        C<Button>("ShowProject").Content = L("btn_open_stick");
        C<Button>("UseProject").Content = L("btn_sync_stick");
        C<Button>("BindProject").Content = L("btn_bind_agy");
        C<TextBlock>("PromptOrderTitle").Text = L("prompt_order_title");
        C<TextBlock>("PromptTitleLabel").Text = L("prompt_name");
        C<TextBlock>("PromptDescLabel").Text = L("prompt_desc");
        C<ComboBoxItem>("ProviderBoth").Content = L("both_ides");
        C<Button>("SavePrompt").Content = L("btn_save_prompt");
        C<Button>("NewPrompt").Content = L("btn_new_prompt");
        C<Button>("DeletePrompt").Content = L("btn_delete");

        C<TextBlock>("SessionsTitle").Text = L("sessions_title");
        C<Button>("StopSessionBtn").Content = L("btn_stop");
        C<Button>("RetrySessionBtn").Content = L("btn_retry");
        C<Button>("ChainNextBtn").Content = L("btn_chain_next");
        C<Button>("CancelChainBtn").Content = L("btn_cancel_chain");
        C<Button>("OpenConversation").Content = L("btn_open_agy_cli");
        C<Button>("OpenAnswer").Content = L("btn_open_answer");
        C<Button>("CopyOutputBtn").Content = L("btn_copy_output");
        C<Button>("DeleteSessionBtn").Content = L("btn_delete_session");
        C<Button>("ClearSessionsBtn").Content = L("btn_clear_sessions");
        C<Button>("TabOutputBtn").Content = L("tab_output");
        C<Button>("CancelShutdownBtn").Content = L("btn_cancel_shutdown");

        C<TextBlock>("CodexAccountsTitle").Text = L("codex_accounts_title");
        C<TextBlock>("CodexAccountsDesc").Text = L("codex_accounts_desc");
        C<Button>("LoginCodexAccount").Content = L("btn_add_codex_account");
        C<Button>("RefreshAccountsBtn").Content = L("btn_refresh");
        C<TextBlock>("AgyAccountsTitle").Text = L("agy_accounts_title");
        C<TextBlock>("AgyAccountsDesc").Text = L("agy_accounts_desc");
        C<Button>("RefreshAgyBtn").Content = L("btn_refresh_sub");

        C<TextBlock>("DevicesTitle").Text = L("devices_title");
        C<Button>("ShowCode").Content = L("btn_show_code");
        C<Button>("PairUsb").Content = L("btn_pair_usb");
        C<Button>("PairUsbCode").Content = L("btn_pair_usb_code");
        C<Button>("FindDevices").Content = L("btn_scan_lan");
        C<Button>("ForgetDevice").Content = L("btn_forget");
        C<TextBlock>("PairCodeLabel").Text = L("pair_code_label");
        C<Button>("PairLan").Content = L("btn_pair_wifi");
        C<TextBlock>("DeviceAddressLabel").Text = L("device_addr_label");

        C<TextBlock>("WifiTitle").Text = L("wifi_title");
        C<Button>("ScanWifi").Content = L("btn_scan_wifi");
        C<Button>("JoinWifi").Content = L("btn_join_wifi");

        C<TextBlock>("CodexRcTitle").Text = L("codex_rc_title");
        C<TextBlock>("CodexRcDesc").Text = L("codex_rc_desc");
        C<Button>("CodexRcStart").Content = L("btn_start_daemon");
        C<Button>("CodexRcPair").Content = L("btn_pair_code");
        C<Button>("CodexRcStop").Content = L("btn_stop");
        C<TextBlock>("AgyRcTitle").Text = L("agy_rc_title");
        C<TextBlock>("AgyRcDesc").Text = L("agy_rc_desc");
        C<TextBlock>("AgyRcNameLabel").Text = L("agy_rc_name_label");
        C<Button>("AgyRcStart").Content = L("btn_start_daemon");
        C<Button>("AgyRcStop").Content = L("btn_stop");
        C<Button>("RefreshRemoteBtn").Content = L("btn_refresh");
        C<TextBlock>("RelayTitle").Text = L("relay_title");
        C<TextBlock>("RelayDesc").Text = L("relay_desc");
        C<Button>("StartLocalRelay").Content = L("btn_start_local_relay");
        C<Button>("StopLocalRelay").Content = L("btn_stop_local_relay");
        C<TextBlock>("RelayAddressLabel").Text = L("relay_addr_label");
        C<Button>("SaveRelay").Content = L("btn_save_url");
        C<Button>("SyncRelay").Content = L("btn_push_stick");
        C<Button>("TestRelay").Content = L("btn_test_conn");
        C<Button>("ExportChannels").Content = L("btn_export_channels");
        C<Button>("ClearRelay").Content = L("btn_clear_url");

        C<TextBlock>("SettingsAutostartTitle").Text = L("settings_auto_title");
        C<TextBlock>("SettingsAutostartDesc").Text = L("settings_auto_desc");
        C<TextBlock>("AutostartStateText").Text = C<CheckBox>("AutostartToggle").IsChecked == true ? L("enabled") : L("disabled");
        C<TextBlock>("SettingsLangTitle").Text = L("settings_lang_title");
        C<TextBlock>("SettingsLangDesc").Text = L("settings_lang_desc");
        C<Button>("SaveLanguageBtn").Content = L("btn_apply_lang");
        C<TextBlock>("SettingsProjectsTitle").Text = L("settings_projects_title");
        C<TextBlock>("SettingsProjectsDesc").Text = L("settings_projects_desc");
        C<Button>("BrowseProjectsRoot").Content = L("btn_browse");
        C<Button>("SaveProjectsRoot").Content = L("btn_save_folder");
        C<TextBlock>("SettingsAboutTitle").Text = L("settings_about_title");
        C<TextBlock>("SettingsAboutDesc").Text = L("settings_about_desc");
        C<Button>("CheckUpdateBtn").Content = L("btn_check_updates");
        C<Button>("OpenWizardBtn").Content = L("btn_open_wizard");
        C<Button>("OpenDataFolderBtn").Content = L("btn_open_data");
        C<Button>("OpenAppFolderBtn").Content = L("btn_open_app");

        C<TextBlock>("FeedbackTitle").Text = L("feedback_title");
        C<TextBlock>("FeedbackDesc").Text = L("feedback_desc");
        C<TextBlock>("FeedbackKindLabel").Text = L("feedback_kind");
        C<Button>("ReportCatBug").Content = L("cat_bug");
        C<Button>("ReportCatFeature").Content = L("cat_feature");
        C<Button>("ReportCatOther").Content = L("cat_other");
        C<TextBlock>("ReportTitleLabel").Text = L("report_title_label");
        C<TextBlock>("ReportContactLabel").Text = L("report_contact_label");
        C<TextBlock>("ReportBodyLabel").Text = L("report_body_label");
        C<TextBlock>("ReportLogsLabel").Text = L("report_logs_label");
        C<TextBlock>("ReportLogsDesc").Text = L("report_logs_desc");
        C<Button>("SendReportBtn").Content = L("btn_send_report");

        C<Button>("FlashStickBtn").Content = L("btn_flash_stick");
        C<Button>("OnbFlashUsbBtn").Content = L("btn_flash_stick");
        C<Button>("AfterUpdateFlashBtn").Content = L("btn_flash_stick");
        C<TextBlock>("AfterUpdateTitle").Text = L("after_update_title");
        C<TextBlock>("AfterUpdateSub").Text = L("after_update_sub");
        C<TextBlock>("AfterUpdateNotes").Text = L("after_update_notes");
        C<TextBlock>("AfterUpdateUsbTitle").Text = L("after_update_usb_title");
        C<TextBlock>("AfterUpdateUsbHint").Text = L("after_update_usb_hint");
        C<Button>("AfterUpdateCloseBtn").Content = L("after_update_btn");

        UpdateHomeEfforts();
        if (lastDevicesResult.Count > 0)
        {
            UpdateStickFirmwareBanner(lastDevicesResult);
            UpdatePowerUi(Obj(lastDevicesResult, "overview"));
        }
        if (updateVersion.Length > 0) RefreshUpdateBannerTexts();
        UpdateOnboardingTexts();
        UpdateHeading();
    }

    static Dictionary<string, Dictionary<string, string>> BuildStrings()
    {
        return new Dictionary<string, Dictionary<string, string>>
        {
            {
                "en", new Dictionary<string, string>
                {
                    { "window_title", "AgentDeck (v2.0) | Remote AI IDE control in your pocket." },
                    { "tagline", "Remote AI IDE control in your pocket." },
                    { "nav_home", "Home" },
                    { "nav_prompts", "Projects & Prompts" },
                    { "nav_sessions", "Sessions" },
                    { "nav_accounts", "Accounts" },
                    { "nav_devices", "Devices" },
                    { "nav_wifi", "Wi-Fi" },
                    { "nav_remote", "Remote Control" },
                    { "nav_feedback", "Feedback" },
                    { "nav_settings", "Settings" },
                    { "sub_home", "Launch prompts or sequential chains, manage IDEs, and control PC auto-shutdown" },
                    { "sub_prompts", "Manage IDE projects and drag-and-drop prompt order for your StickS3" },
                    { "sub_sessions", "Live Antigravity & Codex monitor: commands, tools, subagents, browser actions, and output" },
                    { "sub_accounts", "Auto-synced Codex accounts with 1-click switching and live Antigravity subscription status" },
                    { "sub_devices", "Pair and monitor your M5Stick S3 battery & link over USB or Wi-Fi" },
                    { "sub_wifi", "Configure wireless networks directly on your StickS3" },
                    { "sub_remote", "Official Codex & Antigravity Remote Control + StickS3 away-from-home relay" },
                    { "sub_feedback", "Send Bug Reports & Feature Requests to GitHub via Sentry.io" },
                    { "sub_settings", "Autostart for Wake on LAN, language preferences, and updates" },
                    { "title_idle", "Bridge Idle" },
                    { "title_active_fmt", "{0} Active" },
                    { "title_chain_fmt", "Chain: {0} queued" },
                    { "title_off_idle", "Auto-OFF PC: Off" },
                    { "title_off_armed", "Auto-OFF PC: Armed" },
                    { "title_off_countdown", "Shutdown in {0}s" },
                    { "toggling_shutdown", "Updating auto-shutdown..." },
                    { "cancelling_shutdown", "Cancelling auto-shutdown..." },
                    { "shutdown_armed_note", "PC will shut down automatically once all tasks complete." },
                    { "shutdown_cancelled_note", "Auto-shutdown cancelled." },
                    { "shutdown_armed_title", "Auto-Shutdown Armed" },
                    { "shutdown_settling_title", "Work completed — preparing shutdown countdown..." },
                    { "shutdown_armed_sub", "PC will automatically shut down after all active tasks and prompt chains finish — even if StickS3 disconnects." },
                    { "shutdown_countdown_title", "Shutting down Windows in {0}s" },
                    { "shutdown_countdown_sub", "Click Cancel Shutdown anytime to abort and keep your PC running." },
                    { "shutdown_sent_title", "Shutdown command sent to Windows" },
                    { "shutdown_sent_sub", "Windows is shutting down now." },
                    { "btn_cancel_shutdown", "Cancel Shutdown" },
                    { "btn_auto_off", "Auto-OFF PC" },
                    { "btn_cancel_auto_off", "Cancel Auto-OFF" },
                    { "home_quick_title", "Quick Prompt & Chain Launcher" },
                    { "home_quick_desc", "Run a single prompt or stage sequential chains. Auto-shutdown keeps working even after turning off StickS3." },
                    { "home_ide_label", "AI IDE" },
                    { "home_model_label", "Model" },
                    { "home_effort_label", "Reasoning" },
                    { "effort_default", "Default" },
                    { "home_mode_label", "Mode" },
                    { "home_mode_read", "Read-only (Analysis)" },
                    { "home_mode_write", "Write (Edit files)" },
                    { "home_mode_plan", "Plan & Execute (Auto)" },
                    { "home_project_label", "Project" },
                    { "home_prompt_label", "Prompt" },
                    { "home_shutdown_label", "Auto-OFF PC" },
                    { "btn_send_prompt", "Send Prompt" },
                    { "btn_add_chain_step", "+ Add Step to Chain" },
                    { "btn_clear", "Clear" },
                    { "btn_run_chain_fmt", "Run Chain ({0}+)" },
                    { "chain_draft_fmt", "Chain ({0} staged): {1}" },
                    { "chain_step_added", "Added step {0} to chain draft." },
                    { "chain_started_note", "Prompt chain started! Watching live in Sessions." },
                    { "err_chain_limit", "A chain can contain at most 10 steps." },
                    { "btn_open_sessions", "Sessions" },
                    { "home_live_title", "Live Bridge Status" },
                    { "btn_inspect", "Inspect" },
                    { "home_live_idle", "No active tasks. Launch a prompt or connect StickS3." },
                    { "home_live_active_fmt", "Running: {0} active task(s), {1} queued in chain(s)." },
                    { "home_ides_title", "Installed AI IDEs" },
                    { "home_ides_desc", "Only installed IDEs appear on StickS3. Install missing CLIs in one click." },
                    { "ide_installed", "Installed" },
                    { "ide_missing", "Not installed" },
                    { "ide_not_detected_hint", "Click Install to set up automatically." },
                    { "btn_install", "Install" },
                    { "checking_ides", "Checking installed AI IDEs..." },
                    { "installing_agy", "Installing Antigravity CLI..." },
                    { "installing_codex", "Installing ChatGPT Codex CLI..." },
                    { "ide_installed_fmt", "{0} installed cleanly." },
                    { "home_stick_title", "StickS3 Connection" },
                    { "home_stick_usb_ok", "StickS3 connected via USB-C. Ready to pair or update firmware." },
                    { "home_stick_usb_wait", "Connect M5Stick S3 via USB-C to pair or flash the latest firmware." },
                    { "sending_home_prompt", "Launching prompt session..." },
                    { "prompt_sent", "Prompt started! Watching live in Sessions." },
                    { "err_no_ide_installed", "Install Antigravity CLI or ChatGPT Codex first." },
                    { "err_select_project", "Select a project first." },
                    { "err_enter_prompt", "Enter a prompt first." },
                    { "projects_title", "IDE Projects" },
                    { "filter_all", "All" },
                    { "btn_new_project", "+ New" },
                    { "btn_rename", "Rename" },
                    { "btn_add_folder", "Folder" },
                    { "btn_open_stick", "Open on Stick" },
                    { "btn_sync_stick", "Sync from Stick" },
                    { "btn_bind_agy", "Bind Antigravity ID" },
                    { "global_projects_title", "All Projects (Global)" },
                    { "global_projects_sub", "Available in every project on StickS3" },
                    { "global_hint", "Global prompts appear in all projects, including new random workspaces." },
                    { "select_project", "Select a project" },
                    { "both_ides", "Both IDEs" },
                    { "both_short", "Both" },
                    { "prompt_order_title", "StickS3 Prompt Order (drag rows or use ↑ / ↓ to reorder)" },
                    { "reordering_prompts", "Saving StickS3 prompt order..." },
                    { "prompt_reordered", "Prompt order updated and synced to StickS3." },
                    { "prompt_name", "Prompt Name" },
                    { "prompt_desc", "Short description (what this prompt does)" },
                    { "btn_save_prompt", "Save Prompt" },
                    { "btn_new_prompt", "Clear / New" },
                    { "btn_delete", "Delete" },
                    { "sessions_title", "Sessions & Chains" },
                    { "session_inspector", "Session Inspector" },
                    { "session_chain_fmt", "Chain {0}/{1}" },
                    { "tab_output", "Output" },
                    { "tab_activity_fmt", "Live Activity ({0})" },
                    { "btn_retry", "Retry" },
                    { "btn_chain_next", "+ Chain Next" },
                    { "chaining_next", "Appending step to chain..." },
                    { "chain_next_title", "Chain Next Prompt" },
                    { "chain_next_hint", "This prompt will start automatically right after the selected session finishes." },
                    { "btn_chain_next_confirm", "Add to Chain" },
                    { "chain_appended_note", "Next prompt added to chain!" },
                    { "btn_cancel_chain", "Cancel Chain" },
                    { "btn_copy_output", "Copy Output" },
                    { "copied_output", "Output copied to clipboard." },
                    { "stopping_session", "Stopping session..." },
                    { "session_stopping", "Stop signal sent." },
                    { "retrying_session", "Retrying session..." },
                    { "session_retried", "Session restarted." },
                    { "cancelling_chain", "Stopping prompt chain..." },
                    { "chain_cancelled_note", "Prompt chain cancelled." },
                    { "phase_thinking", "Thinking" },
                    { "phase_command", "Command" },
                    { "phase_tool", "Tool" },
                    { "phase_file_edit", "File Edit" },
                    { "phase_subagent", "Subagent" },
                    { "phase_browser", "Browser" },
                    { "phase_mcp", "MCP" },
                    { "phase_reply", "Response" },
                    { "phase_waiting", "Waiting" },
                    { "phase_retrying", "Retrying" },
                    { "phase_finishing", "Finishing" },
                    { "state_waiting_input", "Question" },
                    { "sending_answer", "Sending answer to agent..." },
                    { "answer_sent", "Answer sent to agent." },
                    { "btn_open_agy_cli", "Open in Antigravity CLI" },
                    { "btn_open_answer", "Open Output File" },
                    { "btn_delete_session", "Delete" },
                    { "btn_clear_sessions", "Clear Finished" },
                    { "deleting_session", "Deleting session..." },
                    { "clearing_sessions", "Clearing finished sessions..." },
                    { "session_deleted", "Session deleted." },
                    { "sessions_cleared_fmt", "Cleared {0} finished session(s)." },
                    { "codex_accounts_title", "Codex Accounts" },
                    { "codex_accounts_desc", "Active Codex login is saved automatically. Add another account below and switch in one click from PC or StickS3." },
                    { "btn_add_codex_account", "+ Add Codex Account" },
                    { "btn_switch_inline", "Switch" },
                    { "agy_accounts_title", "Antigravity Account" },
                    { "agy_accounts_desc", "Antigravity uses your Google account directly inside Antigravity. Active tier and weekly AI quotas are synced below." },
                    { "btn_refresh_sub", "Refresh Subscription" },
                    { "agy_plan_fmt", "{0} PLAN" },
                    { "agy_plan_active", "SIGNED IN" },
                    { "credits_fmt", "{0} credits" },
                    { "agy_quota_empty", "Click Refresh Subscription to query live Antigravity quota." },
                    { "active_badge", "ACTIVE" },
                    { "not_signed_in", "Not signed in" },
                    { "devices_title", "Paired & Connected Devices" },
                    { "stick_screen", "Screen" },
                    { "btn_show_code", "Show Code on Stick" },
                    { "btn_pair_usb", "Pair via USB" },
                    { "btn_pair_usb_code", "USB with Code" },
                    { "btn_scan_lan", "Scan LAN" },
                    { "btn_forget", "Remove Pairing" },
                    { "pair_code_label", "Pairing code shown on Stick" },
                    { "btn_pair_wifi", "Pair Wi-Fi" },
                    { "device_addr_label", "Stick IP Address (optional)" },
                    { "wifi_title", "StickS3 Wi-Fi" },
                    { "btn_scan_wifi", "Scan Wi-Fi on Stick" },
                    { "btn_join_wifi", "Connect Stick" },
                    { "codex_rc_title", "Codex Remote Control" },
                    { "codex_rc_desc", "Enable official Codex Remote Control daemon to send prompts from mobile or StickS3 outside your home network." },
                    { "btn_start_daemon", "Start Daemon" },
                    { "btn_pair_code", "Pairing Code" },
                    { "btn_stop", "Stop" },
                    { "agy_rc_title", "Antigravity Remote Control" },
                    { "agy_rc_desc", "Manage the Antigravity remote-control background daemon so your PC appears in mobile and remote sessions." },
                    { "agy_rc_name_label", "Instance name (optional)" },
                    { "btn_refresh", "Refresh" },
                    { "relay_title", "StickS3 Away-From-Home Encrypted Relay" },
                    { "relay_desc", "Connects your StickS3 over any hotspot to your home PC using end-to-end AES-256-GCM + HMAC encryption. Run the built-in relay behind a tunnel or reverse proxy." },
                    { "btn_start_local_relay", "Start Built-In Relay (8793)" },
                    { "btn_stop_local_relay", "Stop Built-In Relay" },
                    { "relay_addr_label", "Relay Endpoint URL (http://...)" },
                    { "btn_save_url", "Save URL" },
                    { "btn_push_stick", "Push URL to Stick" },
                    { "btn_test_conn", "Test Connection" },
                    { "btn_export_channels", "Export Channel Keys" },
                    { "btn_clear_url", "Clear URL" },
                    { "settings_auto_title", "Background Autostart & Wake on LAN" },
                    { "settings_auto_desc", "Start the silent AgentDeck bridge automatically at Windows login (zero CMD window) so Wake on LAN and StickS3 prompts work immediately." },
                    { "enabled", "Enabled" },
                    { "disabled", "Disabled" },
                    { "autostart_on", "Autostart enabled (runs silently on boot)." },
                    { "autostart_off", "Autostart disabled." },
                    { "settings_lang_title", "Language" },
                    { "settings_lang_desc", "English is enabled by default. Changes apply to Windows and automatically sync to a connected StickS3." },
                    { "btn_apply_lang", "Apply Language" },
                    { "settings_projects_title", "New Projects Directory" },
                    { "settings_projects_desc", "New random-named projects created from StickS3 (e.g. solar-star) or Windows are created inside this directory." },
                    { "btn_browse", "Browse..." },
                    { "btn_save_folder", "Save Folder" },
                    { "settings_about_title", "Updates, Setup Wizard & Folders" },
                    { "settings_about_desc", "AgentDeck v2.0 · Check GitHub Releases for updates, run the setup wizard, or open local folders." },
                    { "btn_check_updates", "Check for Updates" },
                    { "checking_updates", "Checking GitHub Releases for updates..." },
                    { "no_updates_found", "You are already on the latest version." },
                    { "update_found_note", "New version v{0} is available." },
                    { "update_avail_title", "AgentDeck v{0} is available" },
                    { "update_avail_sub", "Download and install automatically from GitHub Releases." },
                    { "btn_update_to", "Update to v{0}" },
                    { "update_downloading_title", "Downloading AgentDeck v{0}..." },
                    { "btn_downloading", "Downloading..." },
                    { "downloading_update", "Starting update download..." },
                    { "update_dl_pct", "{0}% downloaded ({1:F1} / {2:F1} MB)" },
                    { "update_dl_mb", "{0:F1} MB downloaded..." },
                    { "update_ready_title", "AgentDeck v{0} ready to install" },
                    { "update_ready_sub", "Restart AgentDeck to install silently and remove temporary setup files." },
                    { "btn_restart_update", "Restart to update" },
                    { "applying_update", "Applying update and restarting..." },
                    { "btn_open_wizard", "Run Setup Wizard" },
                    { "btn_open_data", "Open Data Folder" },
                    { "btn_open_app", "Open App Folder" },
                    { "onb_step_badge", "Step {0} of 3" },
                    { "onb_step1_title", "Welcome to AgentDeck" },
                    { "onb_step1_sub", "Choose your preferred language for AgentDeck on Windows and StickS3." },
                    { "onb_lang_prompt", "Interface Language" },
                    { "onb_lang_en_sub", "Default interface language" },
                    { "onb_lang_pl_sub", "Polski interfejs na PC i Sticku" },
                    { "onb_step2_title", "Start Automatically with Windows?" },
                    { "onb_step2_sub", "AgentDeck always runs silently without any CMD window. Choose if Windows should start it automatically (recommended for Wake on LAN)." },
                    { "onb_auto_title", "Add AgentDeck to Windows Autostart" },
                    { "onb_auto_desc", "Enabled by default — launches the silent background bridge on sign-in so StickS3 and Wake on LAN are always ready." },
                    { "onb_auto_hint", "Recommended: keeps your StickS3 ready whenever Windows boots or wakes." },
                    { "onb_step3_title", "Connect Your StickS3" },
                    { "onb_step3_sub", "Pair via USB in one click so Wi-Fi, battery monitoring, and Wake on LAN keys are configured." },
                    { "onb_stick_usb_paired", "StickS3 connected & paired via USB" },
                    { "onb_stick_usb_unpaired", "StickS3 detected on USB (not paired yet)" },
                    { "onb_stick_wifi", "StickS3 reachable over Wi-Fi" },
                    { "onb_stick_missing", "No StickS3 detected on USB right now" },
                    { "onb_stick_ready_desc", "Your StickS3 is paired and ready. Click Finish to enter AgentDeck." },
                    { "onb_stick_pair_desc", "Click Pair via USB below to bond this PC and StickS3 securely." },
                    { "onb_stick_wait_desc", "Plug in your StickS3 via USB-C to pair now, or finish setup and pair anytime in Devices." },
                    { "onb_skip", "Skip Setup" },
                    { "onb_back", "Back" },
                    { "onb_continue", "Continue" },
                    { "onb_finish", "Get Started" },
                    { "ready", "Ready" },
                    { "loading_projects", "Loading IDE projects..." },
                    { "saving_prompt", "Saving prompt..." },
                    { "deleting_prompt", "Deleting prompt..." },
                    { "creating_project", "Creating project..." },
                    { "renaming_project", "Renaming project..." },
                    { "select_project_folder", "Select project folder" },
                    { "bind_agy_title", "Antigravity Project" },
                    { "agy_project_id_label", "Antigravity Project ID" },
                    { "sending_project", "Opening project on Stick..." },
                    { "switching_account", "Switching Codex account..." },
                    { "launching_login", "Launching Codex login..." },
                    { "removing_account", "Removing saved account..." },
                    { "loading_accounts", "Refreshing accounts..." },
                    { "opening_pairing", "Opening pairing screen..." },
                    { "enter_stick_code", "Enter the code shown on Stick." },
                    { "pairing_usb", "Pairing via USB..." },
                    { "checking_usb_code", "Checking USB code..." },
                    { "checking_wifi_code", "Checking Wi-Fi code..." },
                    { "scanning_lan", "Scanning LAN for StickS3..." },
                    { "removing_pairing", "Removing pairing..." },
                    { "scanning_wifi", "Stick is scanning Wi-Fi..." },
                    { "sending_wifi", "Sending Wi-Fi credentials..." },
                    { "rc_working", "Updating Remote Control..." },
                    { "starting_local_relay", "Starting built-in relay..." },
                    { "stopping_local_relay", "Stopping built-in relay..." },
                    { "saving_relay", "Saving relay URL..." },
                    { "clearing_relay", "Clearing relay URL..." },
                    { "syncing_stick", "Syncing relay to Stick..." },
                    { "relay_synced", "Relay settings saved on Stick." },
                    { "exporting_channels", "Reading channel keys..." },
                    { "testing_relay", "Testing relay connection..." },
                    { "applying_language", "Applying language..." },
                    { "saving_settings", "Saving settings..." },
                    { "opening_conv", "Opening Antigravity conversation..." },
                    { "conv_opened", "Conversation opened in terminal." },
                    { "select_project_first", "Select a project first." },
                    { "prompt_saved", "Prompt saved on PC and synced to Stick." },
                    { "select_saved_prompt", "Select a saved prompt first." },
                    { "confirm_delete_prompt", "Delete the saved prompt?" },
                    { "prompt_deleted", "Prompt deleted." },
                    { "new_project_title", "New Project" },
                    { "new_project_name_hint", "Folder name (leave blank for a random 2-word English name like solar-star):" },
                    { "new_project_ide", "Target IDE" },
                    { "cancel", "Cancel" },
                    { "create", "Create" },
                    { "save", "Save" },
                    { "project_created", "Project created: {0}" },
                    { "select_folder_rename", "Select a folder project to rename." },
                    { "rename_project_title", "Rename Project" },
                    { "rename_project_label", "New folder name:" },
                    { "project_renamed", "Project renamed to {0}" },
                    { "folder_saved", "Folder saved." },
                    { "binding_saved", "Antigravity binding saved." },
                    { "project_opened_stick", "Project opened on Stick." },
                    { "select_project_on_stick", "Open a project on Stick first." },
                    { "confirm_discard_prompt", "Switch project without saving the current prompt?" },
                    { "opening_stick_project", "Opening project from Stick..." },
                    { "project_synced", "Project synced from Stick." },
                    { "accounts_refreshed", "Accounts refreshed." },
                    { "account_switched", "Switched active Codex account." },
                    { "login_launched", "Sign in inside the Codex login window — AgentDeck will add the account automatically." },
                    { "account_removed", "Profile removed from AgentDeck." },
                    { "rc_updated", "Remote Control status updated." },
                    { "rc_active", "Active" },
                    { "rc_active_ide", "Active (IDE)" },
                    { "rc_inactive", "Inactive" },
                    { "rc_registered", "Registered" },
                    { "rc_not_registered", "Not registered" },
                    { "unavailable", "Unavailable" },
                    { "rc_desktop_connected", "Codex Desktop remote connected" },
                    { "rc_daemon_running", "Remote-control daemon running" },
                    { "rc_codex_idle", "Remote-control daemon not started." },
                    { "rc_instance", "Instance" },
                    { "rc_agy_idle", "Daemon is not registered." },
                    { "local_relay_running", "Built-in relay running on 127.0.0.1:{0}" },
                    { "local_relay_stopped", "Built-in relay stopped" },
                    { "local_relay_started", "Built-in relay started on port 8793." },
                    { "lang_saved", "Language saved." },
                    { "lang_saved_synced", "Language saved and synced to StickS3." },
                    { "settings_saved", "Settings saved." },
                    { "stick_paired", "StickS3 paired successfully." },
                    { "select_stick_enter_code", "Select a Stick and enter the pairing code." },
                    { "no_sticks_found", "No StickS3 devices found on this network." },
                    { "select_paired_stick", "Select a paired Stick first." },
                    { "pairing_removed", "Pairing removed from PC." },
                    { "stick_usb", "Stick via USB" },
                    { "stick_remote", "Stick via Internet" },
                    { "stick_wifi", "Stick via Wi-Fi" },
                    { "bridge_ready", "Bridge Ready" },
                    { "bridge_offline", "Bridge Offline" },
                    { "waiting_stick", "Waiting for StickS3" },
                    { "stick_no_wifi", "Stick without Wi-Fi" },
                    { "internet", "Internet" },
                    { "code_active", "Pairing code active" },
                    { "state_queued", "Queued" },
                    { "state_running", "Running" },
                    { "state_finished", "Completed" },
                    { "state_cancelled", "Stopped" },
                    { "state_limit", "Rate limit" },
                    { "state_error", "Error" },
                    { "reading_session", "Reading session..." },
                    { "select_session_prompt", "Select a session to inspect live tools, subagents, commands, and output." },
                    { "full_answer_in_file", "Full output is saved in file." },
                    { "connect_stick_first", "Connect a Stick first." },
                    { "select_network", "Select a network from the list" },
                    { "select_network_err", "Select a Wi-Fi network." },
                    { "password_length_err", "Enter a password between 8 and 63 bytes." },
                    { "stick_joining_wifi", "Stick is connecting to Wi-Fi..." },
                    { "reading_networks", "Reading scanned networks..." },
                    { "secured", "Secured" },
                    { "open", "Open" },
                    { "no_networks_found", "Stick found no networks." },
                    { "scan_timeout", "No Wi-Fi scan reply from Stick." },
                    { "wifi_connected", "Stick connected to Wi-Fi." },
                    { "check_stick_screen", "Check the Wi-Fi status on Stick screen." },
                    { "relay_saved", "Relay saved on PC. Push URL to Stick when ready." },
                    { "pair_for_channels", "Pair Stick first to create channel keys." },
                    { "channels_saved", "Channel keys exported for relay server." },
                    { "relay_reachable", "Stick is reachable through the relay!" },
                    { "relay_unreachable", "No response via relay. Check URL and Stick Wi-Fi." },
                    { "feedback_title", "Send Feedback to GitHub via Sentry.io" },
                    { "feedback_desc", "Submit a Bug Report, Feature Request, or idea via Sentry.io connected to github.com/natekk8/agentdeck." },
                    { "feedback_kind", "Category" },
                    { "cat_bug", "Bug Report" },
                    { "cat_feature", "Feature Request" },
                    { "cat_other", "Other" },
                    { "report_title_label", "Title" },
                    { "report_contact_label", "Your GitHub @handle or email (optional)" },
                    { "report_body_label", "Description (steps to reproduce or feature details)" },
                    { "report_logs_label", "Attach sanitized diagnostics" },
                    { "report_logs_desc", "Includes recent bridge error logs with local usernames redacted." },
                    { "btn_send_report", "Submit Report" },
                    { "sending_report", "Sending report to Sentry.io..." },
                    { "report_sent_fmt", "Report sent cleanly! Sentry Event: {0}" },
                    { "stick_fw_title", "StickS3 firmware update required (v{0})" },
                    { "stick_fw_title_ver", "StickS3 update: v{0} -> v{1}" },
                    { "stick_fw_usb_sub", "Connect your M5Stick S3 via USB-C to update firmware to v{0}." },
                    { "stick_fw_ready_sub", "StickS3 detected on USB. Click to flash firmware v{0} automatically." },
                    { "stick_fw_btn", "Update StickS3 Firmware" },
                    { "stick_fw_flashing", "Flashing StickS3 firmware via USB..." },
                    { "stick_fw_flashed_fmt", "StickS3 updated cleanly to v{0}." },
                    { "btn_flash_stick", "Flash Firmware (USB)" },
                    { "after_update_title", "Updated to AgentDeck v2.0" },
                    { "after_update_sub", "Everything is ready on your PC." },
                    { "after_update_notes", "• Obsidian Glass Studio UI with drag-and-drop StickS3 prompt ordering\n• Sequential Prompt Chains & autonomous PC Auto-Shutdown (works even if Stick disconnects)\n• Deep Antigravity & Codex Live Activity monitor (commands, tools, browser, subagents)" },
                    { "after_update_usb_title", "StickS3 Firmware (v2.0.0)" },
                    { "after_update_usb_hint", "Plug in M5Stick S3 via USB-C to flash the latest AgentDeck v2.0.0 firmware." },
                    { "after_update_btn", "Continue to AgentDeck" }
                }
            },
            {
                "pl", new Dictionary<string, string>
                {
                    { "window_title", "AgentDeck (v2.0) | Zdalne sterowanie AI IDE w Twojej kieszeni." },
                    { "tagline", "Zdalne sterowanie AI IDE w Twojej kieszeni." },
                    { "nav_home", "Strona główna" },
                    { "nav_prompts", "Projekty i prompty" },
                    { "nav_sessions", "Sesje" },
                    { "nav_accounts", "Konta" },
                    { "nav_devices", "Urządzenia" },
                    { "nav_wifi", "Wi-Fi" },
                    { "nav_remote", "Remote Control" },
                    { "nav_feedback", "Zgłoszenia" },
                    { "nav_settings", "Ustawienia" },
                    { "sub_home", "Uruchamiaj prompty lub ciągi kroków, zarządzaj IDE i wyłączaniem PC po pracy" },
                    { "sub_prompts", "Zarządzaj projektami i ustawiaj kolejność wyświetlania promptów na StickS3" },
                    { "sub_sessions", "Podgląd na żywo Antigravity i Codex: komendy, narzędzia, subagenty, przeglądarka i wynik" },
                    { "sub_accounts", "Automatycznie zapisane konta Codex (1-klik) oraz podgląd subskrypcji Antigravity" },
                    { "sub_devices", "Paruj StickS3 i sprawdzaj poziom baterii oraz połączenie USB / Wi-Fi" },
                    { "sub_wifi", "Konfiguruj sieci bezprzewodowe bezpośrednio na StickS3" },
                    { "sub_remote", "Natywne Remote Control Codex i Antigravity + szyfrowany przekaźnik StickS3" },
                    { "sub_feedback", "Wysyłaj Bug Reporty i Feature Requesty na GitHub przez Sentry.io" },
                    { "sub_settings", "Autostart pod Wake on LAN, język i aktualizacje" },
                    { "title_idle", "Mostek gotowy" },
                    { "title_active_fmt", "Aktywne: {0}" },
                    { "title_chain_fmt", "Ciąg: {0} w kolejce" },
                    { "title_off_idle", "Auto-OFF PC: Wył." },
                    { "title_off_armed", "Auto-OFF PC: Uzbrojone" },
                    { "title_off_countdown", "Wyłączenie za {0}s" },
                    { "toggling_shutdown", "Zmieniam auto-wyłączanie..." },
                    { "cancelling_shutdown", "Anuluję wyłączanie PC..." },
                    { "shutdown_armed_note", "Komputer wyłączy się automatycznie po zakończeniu całej pracy." },
                    { "shutdown_cancelled_note", "Anulowano automatyczne wyłączenie." },
                    { "shutdown_armed_title", "Auto-wyłączenie PC uzbrojone" },
                    { "shutdown_settling_title", "Praca zakończona — przygotowywanie odliczania..." },
                    { "shutdown_armed_sub", "Komputer wyłączy się sam po zakończeniu wszystkich zadań i ciągów — nawet gdy wyłączysz StickS3." },
                    { "shutdown_countdown_title", "Wyłączenie Windows za {0}s" },
                    { "shutdown_countdown_sub", "Kliknij Anuluj wyłączenie w dowolnej chwili, aby zostawić komputer włączony." },
                    { "shutdown_sent_title", "Wysłano polecenie wyłączenia Windows" },
                    { "shutdown_sent_sub", "System Windows zamyka się teraz." },
                    { "btn_cancel_shutdown", "Anuluj wyłączenie" },
                    { "btn_auto_off", "Wyłącz PC po pracy" },
                    { "btn_cancel_auto_off", "Anuluj Auto-OFF" },
                    { "home_quick_title", "Szybki prompt i ciąg zadań" },
                    { "home_quick_desc", "Wyślij pojedynczy prompt lub zbuduj ciąg kolejnych kroków. Auto-OFF działa dalej nawet po odłączeniu StickS3." },
                    { "home_ide_label", "AI IDE" },
                    { "home_model_label", "Model" },
                    { "home_effort_label", "Poziom myślenia" },
                    { "effort_default", "Domyślny" },
                    { "home_mode_label", "Tryb" },
                    { "home_mode_read", "Tylko odczyt (Analiza)" },
                    { "home_mode_write", "Zapis (Edycja plików)" },
                    { "home_mode_plan", "Planuj i wykonaj (Auto)" },
                    { "home_project_label", "Projekt" },
                    { "home_prompt_label", "Treść promptu" },
                    { "home_shutdown_label", "Wyłącz PC po pracy" },
                    { "btn_send_prompt", "Wyślij prompt" },
                    { "btn_add_chain_step", "+ Dodaj krok do ciągu" },
                    { "btn_clear", "Wyczyść" },
                    { "btn_run_chain_fmt", "Uruchom ciąg ({0}+)" },
                    { "chain_draft_fmt", "Ciąg ({0} kroków): {1}" },
                    { "chain_step_added", "Dodano krok {0} do ciągu." },
                    { "chain_started_note", "Uruchomiono ciąg promptów! Podgląd na żywo w Sesjach." },
                    { "err_chain_limit", "Ciąg może mieć maksymalnie 10 kroków." },
                    { "btn_open_sessions", "Sesje" },
                    { "home_live_title", "Stan mostka na żywo" },
                    { "btn_inspect", "Podgląd" },
                    { "home_live_idle", "Brak aktywnych zadań. Wyślij prompt lub użyj StickS3." },
                    { "home_live_active_fmt", "Pracuje: {0} aktywnych zadań, {1} w kolejce ciągu." },
                    { "home_ides_title", "Zainstalowane AI IDE" },
                    { "home_ides_desc", "Na StickS3 wyświetlają się tylko zainstalowane IDE. Brakujące zainstalujesz jednym kliknięciem." },
                    { "ide_installed", "Zainstalowano" },
                    { "ide_missing", "Brak" },
                    { "ide_not_detected_hint", "Kliknij Zainstaluj, aby zainstalować automatycznie." },
                    { "btn_install", "Zainstaluj" },
                    { "checking_ides", "Sprawdzam zainstalowane AI IDE..." },
                    { "installing_agy", "Instaluję Antigravity CLI..." },
                    { "installing_codex", "Instaluję ChatGPT Codex CLI..." },
                    { "ide_installed_fmt", "Zainstalowano {0}." },
                    { "home_stick_title", "Połączenie StickS3" },
                    { "home_stick_usb_ok", "StickS3 podłączony przez USB-C. Gotowy do parowania lub aktualizacji." },
                    { "home_stick_usb_wait", "Podłącz M5Stick S3 kablem USB-C, aby sparować lub wgrać najnowszy firmware." },
                    { "sending_home_prompt", "Uruchamiam sesję..." },
                    { "prompt_sent", "Prompt uruchomiony! Podgląd na żywo w Sesjach." },
                    { "err_no_ide_installed", "Najpierw zainstaluj Antigravity CLI lub ChatGPT Codex." },
                    { "err_select_project", "Najpierw wybierz projekt." },
                    { "err_enter_prompt", "Najpierw wpisz treść promptu." },
                    { "projects_title", "Projekty z IDE" },
                    { "filter_all", "Wszystkie" },
                    { "btn_new_project", "+ Nowy" },
                    { "btn_rename", "Zmień nazwę" },
                    { "btn_add_folder", "Folder" },
                    { "btn_open_stick", "Otwórz na Sticku" },
                    { "btn_sync_stick", "Wczytaj ze Sticka" },
                    { "btn_bind_agy", "Powiąż z Antigravity" },
                    { "global_projects_title", "Wszystkie projekty (globalne)" },
                    { "global_projects_sub", "Dostępne w każdym projekcie na StickS3" },
                    { "global_hint", "Prompty globalne są widoczne w każdym projekcie, także w losowych ze Sticka." },
                    { "select_project", "Wybierz projekt" },
                    { "both_ides", "Oba IDE" },
                    { "both_short", "Oba" },
                    { "prompt_order_title", "Kolejność promptów na StickS3 (przeciągnij wiersze lub użyj ↑ / ↓)" },
                    { "reordering_prompts", "Zapisuję kolejność promptów na StickS3..." },
                    { "prompt_reordered", "Kolejność promptów zaktualizowana na StickS3." },
                    { "prompt_name", "Nazwa promptu" },
                    { "prompt_desc", "Krótki opis (co robi ten prompt)" },
                    { "btn_save_prompt", "Zapisz prompt" },
                    { "btn_new_prompt", "Nowy / wyczyść" },
                    { "btn_delete", "Usuń" },
                    { "sessions_title", "Sesje i ciągi" },
                    { "session_inspector", "Inspektor sesji" },
                    { "session_chain_fmt", "Ciąg {0}/{1}" },
                    { "tab_output", "Wynik" },
                    { "tab_activity_fmt", "Aktywność ({0})" },
                    { "btn_retry", "Ponów" },
                    { "btn_chain_next", "+ Kolejny w ciągu" },
                    { "chaining_next", "Dodaję krok do ciągu..." },
                    { "chain_next_title", "Dodaj kolejny prompt do ciągu" },
                    { "chain_next_hint", "Ten prompt uruchomi się automatycznie od razu po zakończeniu wybranej sesji." },
                    { "btn_chain_next_confirm", "Dodaj do ciągu" },
                    { "chain_appended_note", "Dodano kolejny prompt do ciągu!" },
                    { "btn_cancel_chain", "Zatrzymaj ciąg" },
                    { "btn_copy_output", "Kopiuj wynik" },
                    { "copied_output", "Skopiowano wynik do schowka." },
                    { "stopping_session", "Zatrzymuję sesję..." },
                    { "session_stopping", "Wysłano sygnał zatrzymania." },
                    { "retrying_session", "Ponawiam sesję..." },
                    { "session_retried", "Sesja uruchomiona ponownie." },
                    { "cancelling_chain", "Zatrzymuję ciąg promptów..." },
                    { "chain_cancelled_note", "Zatrzymano ciąg promptów." },
                    { "phase_thinking", "Analizuje" },
                    { "phase_command", "Komenda" },
                    { "phase_tool", "Narzędzie" },
                    { "phase_file_edit", "Edycja plików" },
                    { "phase_subagent", "Subagent" },
                    { "phase_browser", "Przeglądarka" },
                    { "phase_mcp", "MCP" },
                    { "phase_reply", "Odpowiedź" },
                    { "phase_waiting", "Oczekuje" },
                    { "phase_retrying", "Ponawia" },
                    { "phase_finishing", "Kończy" },
                    { "state_waiting_input", "Pytanie" },
                    { "sending_answer", "Wysyłam odpowiedź do agenta..." },
                    { "answer_sent", "Przekazano odpowiedź do agenta." },
                    { "btn_open_agy_cli", "Otwórz w Antigravity CLI" },
                    { "btn_open_answer", "Otwórz plik odpowiedzi" },
                    { "btn_delete_session", "Usuń" },
                    { "btn_clear_sessions", "Wyczyść ukończone" },
                    { "deleting_session", "Usuwam sesję..." },
                    { "clearing_sessions", "Czyszczę ukończone sesje..." },
                    { "session_deleted", "Usunięto sesję." },
                    { "sessions_cleared_fmt", "Usunięto ukończone sesje: {0}." },
                    { "codex_accounts_title", "Konta Codex" },
                    { "codex_accounts_desc", "Aktywne konto zapisuje się automatycznie. Dodaj kolejne logowanie i przełączaj jednym kliknięciem z PC lub Sticka." },
                    { "btn_add_codex_account", "+ Dodaj konto Codex" },
                    { "btn_switch_inline", "Przełącz" },
                    { "agy_accounts_title", "Konto Antigravity" },
                    { "agy_accounts_desc", "Antigravity używa Twojego konta Google bezpośrednio w IDE. Poniżej widoczny jest plan subskrypcji i limity AI." },
                    { "btn_refresh_sub", "Odśwież subskrypcję" },
                    { "agy_plan_fmt", "PLAN {0}" },
                    { "agy_plan_active", "ZALOGOWANO" },
                    { "credits_fmt", "{0} kredytów" },
                    { "agy_quota_empty", "Kliknij Odśwież subskrypcję, aby pobrać aktualne limity Antigravity." },
                    { "active_badge", "AKTYWNE" },
                    { "not_signed_in", "Niezalogowano" },
                    { "devices_title", "Sparowane i podłączone urządzenia" },
                    { "stick_screen", "Ekran" },
                    { "btn_show_code", "Pokaż kod na Sticku" },
                    { "btn_pair_usb", "Sparuj przez USB" },
                    { "btn_pair_usb_code", "USB z kodem" },
                    { "btn_scan_lan", "Szukaj w sieci" },
                    { "btn_forget", "Usuń parowanie" },
                    { "pair_code_label", "Kod parowania ze Sticka" },
                    { "btn_pair_wifi", "Sparuj Wi-Fi" },
                    { "device_addr_label", "Adres IP Sticka (opcjonalnie)" },
                    { "wifi_title", "Wi-Fi StickS3" },
                    { "btn_scan_wifi", "Skanuj Wi-Fi na Sticku" },
                    { "btn_join_wifi", "Połącz Stick" },
                    { "codex_rc_title", "Codex Remote Control" },
                    { "codex_rc_desc", "Uruchom oficjalnego demona Codex Remote Control, aby wysyłać prompty z telefonu lub Sticka poza domem." },
                    { "btn_start_daemon", "Uruchom demona" },
                    { "btn_pair_code", "Kod parowania" },
                    { "btn_stop", "Zatrzymaj" },
                    { "agy_rc_title", "Antigravity Remote Control" },
                    { "agy_rc_desc", "Zarządzaj demonem Antigravity Remote Control, aby PC był dostępny w aplikacji mobilnej i zdalnych sesjach." },
                    { "agy_rc_name_label", "Nazwa instancji (opcjonalnie)" },
                    { "btn_refresh", "Odśwież" },
                    { "relay_title", "Szyfrowany przekaźnik StickS3 poza domem" },
                    { "relay_desc", "Łączy StickS3 z domowym PC przez hotspot za pomocą szyfrowania AES-256-GCM + HMAC. Uruchom wbudowany przekaźnik za tunelem." },
                    { "btn_start_local_relay", "Uruchom lokalny przekaźnik (8793)" },
                    { "btn_stop_local_relay", "Zatrzymaj przekaźnik" },
                    { "relay_addr_label", "Adres przekaźnika (http://...)" },
                    { "btn_save_url", "Zapisz adres" },
                    { "btn_push_stick", "Przekaż na Stick" },
                    { "btn_test_conn", "Testuj połączenie" },
                    { "btn_export_channels", "Eksportuj klucze kanałów" },
                    { "btn_clear_url", "Wyczyść adres" },
                    { "settings_auto_title", "Autostart w tle i Wake on LAN" },
                    { "settings_auto_desc", "Uruchamiaj cichy mostek AgentDeck przy starcie Windows (bez żadnego okna CMD), aby Wake on LAN i prompty ze Sticka zawsze działały." },
                    { "enabled", "Włączony" },
                    { "disabled", "Wyłączony" },
                    { "autostart_on", "Autostart włączony (uruchamianie w tle bez okna CMD)." },
                    { "autostart_off", "Autostart wyłączony." },
                    { "settings_lang_title", "Język aplikacji" },
                    { "settings_lang_desc", "Domyślnie ustawiony jest język angielski. Zmiana aktualizuje Windows i automatycznie synchronizuje StickS3." },
                    { "btn_apply_lang", "Zastosuj język" },
                    { "settings_projects_title", "Folder nowych projektów" },
                    { "settings_projects_desc", "Nowe losowe projekty tworzone ze StickS3 (np. solar-star) lub w Windows powstają w tym katalogu." },
                    { "btn_browse", "Przeglądaj..." },
                    { "btn_save_folder", "Zapisz folder" },
                    { "settings_about_title", "Aktualizacje, kreator i foldery" },
                    { "settings_about_desc", "AgentDeck v2.0 · Sprawdzaj aktualizacje na GitHub Releases, uruchom kreator lub otwórz lokalne foldery." },
                    { "btn_check_updates", "Sprawdź aktualizacje" },
                    { "checking_updates", "Sprawdzam nowe wydania na GitHub..." },
                    { "no_updates_found", "Używasz już najnowszej wersji." },
                    { "update_found_note", "Dostępna nowa wersja v{0}." },
                    { "update_avail_title", "Dostępna aktualizacja AgentDeck v{0}" },
                    { "update_avail_sub", "Pobierz i zainstaluj automatycznie z GitHub Releases." },
                    { "btn_update_to", "Zaktualizuj do v{0}" },
                    { "update_downloading_title", "Pobieranie AgentDeck v{0}..." },
                    { "btn_downloading", "Pobieranie..." },
                    { "downloading_update", "Rozpoczynam pobieranie aktualizacji..." },
                    { "update_dl_pct", "Pobrano {0}% ({1:F1} / {2:F1} MB)" },
                    { "update_dl_mb", "Pobrano {0:F1} MB..." },
                    { "update_ready_title", "Wersja AgentDeck v{0} gotowa do instalacji" },
                    { "update_ready_sub", "Uruchom ponownie, aby zainstalować w tle i usunąć tymczasowy instalator." },
                    { "btn_restart_update", "Uruchom ponownie" },
                    { "applying_update", "Aktualizuję i uruchamiam ponownie..." },
                    { "btn_open_wizard", "Uruchom kreator startowy" },
                    { "btn_open_data", "Otwórz folder danych" },
                    { "btn_open_app", "Otwórz folder aplikacji" },
                    { "onb_step_badge", "Krok {0} z 3" },
                    { "onb_step1_title", "Witaj w AgentDeck" },
                    { "onb_step1_sub", "Wybierz preferowany język interfejsu w Windows oraz na StickS3." },
                    { "onb_lang_prompt", "Język interfejsu" },
                    { "onb_lang_en_sub", "Domyślny język angielski" },
                    { "onb_lang_pl_sub", "Polski interfejs na PC i Sticku" },
                    { "onb_step2_title", "Dodać do autostartu Windows?" },
                    { "onb_step2_sub", "AgentDeck zawsze uruchamia się po cichu bez okna CMD. Wybierz, czy włączać go automatycznie z systemem (dla Wake on LAN)." },
                    { "onb_auto_title", "Włącz autostart AgentDeck przy starcie Windows" },
                    { "onb_auto_desc", "Domyślnie włączone — cichy mostek startuje w tle razem z Windows, żeby StickS3 i Wake on LAN działały od razu." },
                    { "onb_auto_hint", "Zalecane: komputer jest zawsze gotowy po wybudzeniu przez Wake on LAN." },
                    { "onb_step3_title", "Połącz swoje StickS3" },
                    { "onb_step3_sub", "Sparuj przez USB jednym kliknięciem, aby odblokować podgląd baterii, Wi-Fi i Wake on LAN." },
                    { "onb_stick_usb_paired", "StickS3 podłączone i sparowane przez USB" },
                    { "onb_stick_usb_unpaired", "Wykryto StickS3 na USB (nieparowane)" },
                    { "onb_stick_wifi", "StickS3 połączone przez Wi-Fi" },
                    { "onb_stick_missing", "StickS3 nie jest teraz podłączone przez USB" },
                    { "onb_stick_ready_desc", "Twój StickS3 jest sparowany i gotowy. Kliknij Rozpocznij, aby wejść do aplikacji." },
                    { "onb_stick_pair_desc", "Kliknij Sparuj przez USB poniżej, aby bezpiecznie powiązać PC ze StickS3." },
                    { "onb_stick_wait_desc", "Podłącz StickS3 kablem USB-C, aby sparować teraz, albo zakończ i sparuj później w Urządzeniach." },
                    { "onb_skip", "Pomiń" },
                    { "onb_back", "Wstecz" },
                    { "onb_continue", "Dalej" },
                    { "onb_finish", "Rozpocznij" },
                    { "ready", "Gotowe" },
                    { "loading_projects", "Wczytuję projekty z IDE..." },
                    { "saving_prompt", "Zapisuję prompt..." },
                    { "deleting_prompt", "Usuwam prompt..." },
                    { "creating_project", "Tworzę projekt..." },
                    { "renaming_project", "Zmieniam nazwę projektu..." },
                    { "select_project_folder", "Wybierz katalog projektu" },
                    { "bind_agy_title", "Projekt Antigravity" },
                    { "agy_project_id_label", "Identyfikator projektu Antigravity" },
                    { "sending_project", "Otwieram projekt na Sticku..." },
                    { "switching_account", "Przełączam konto Codex..." },
                    { "launching_login", "Otwieram logowanie Codex..." },
                    { "removing_account", "Usuwam profil konta..." },
                    { "loading_accounts", "Odświeżam konta..." },
                    { "opening_pairing", "Otwieram ekran parowania..." },
                    { "enter_stick_code", "Wpisz kod wyświetlony na Sticku." },
                    { "pairing_usb", "Paruję przez USB..." },
                    { "checking_usb_code", "Sprawdzam kod USB..." },
                    { "checking_wifi_code", "Sprawdzam kod Wi-Fi..." },
                    { "scanning_lan", "Szukam Sticka w sieci..." },
                    { "removing_pairing", "Usuwam parowanie..." },
                    { "scanning_wifi", "Stick skanuje Wi-Fi..." },
                    { "sending_wifi", "Przekazuję ustawienia Wi-Fi..." },
                    { "rc_working", "Aktualizuję Remote Control..." },
                    { "starting_local_relay", "Uruchamiam lokalny przekaźnik..." },
                    { "stopping_local_relay", "Zatrzymuję lokalny przekaźnik..." },
                    { "saving_relay", "Zapisuję adres przekaźnika..." },
                    { "clearing_relay", "Czyszczę adres..." },
                    { "syncing_stick", "Synchronizuję Stick..." },
                    { "relay_synced", "Zapisano ustawienia przekaźnika na Sticku." },
                    { "exporting_channels", "Odczytuję klucze kanałów..." },
                    { "testing_relay", "Testuję połączenie przekaźnika..." },
                    { "applying_language", "Zmieniam język..." },
                    { "saving_settings", "Zapisuję ustawienia..." },
                    { "opening_conv", "Otwieram rozmowę Antigravity..." },
                    { "conv_opened", "Rozmowa otwarta w terminalu." },
                    { "select_project_first", "Najpierw wybierz projekt." },
                    { "prompt_saved", "Prompt zapisany na PC i dostępny na Sticku." },
                    { "select_saved_prompt", "Wybierz zapisany prompt." },
                    { "confirm_delete_prompt", "Usunąć zapisany prompt?" },
                    { "prompt_deleted", "Prompt usunięty." },
                    { "new_project_title", "Nowy projekt" },
                    { "new_project_name_hint", "Nazwa folderu (zostaw puste, aby wylosować dwuwyrazową nazwę np. solar-star):" },
                    { "new_project_ide", "Docelowe IDE" },
                    { "cancel", "Anuluj" },
                    { "create", "Utwórz" },
                    { "save", "Zapisz" },
                    { "project_created", "Utworzono projekt: {0}" },
                    { "select_folder_rename", "Wybierz folder projektu do zmiany nazwy." },
                    { "rename_project_title", "Zmień nazwę projektu" },
                    { "rename_project_label", "Nowa nazwa folderu:" },
                    { "project_renamed", "Zmieniono nazwę projektu na {0}" },
                    { "folder_saved", "Folder zapisany." },
                    { "binding_saved", "Powiązanie Antigravity zapisane." },
                    { "project_opened_stick", "Projekt otwarty na Sticku." },
                    { "select_project_on_stick", "Wybierz projekt na Sticku." },
                    { "confirm_discard_prompt", "Zmienić projekt bez zapisu bieżącego promptu?" },
                    { "opening_stick_project", "Otwieram projekt ze Sticka..." },
                    { "project_synced", "Projekt zsynchronizowany." },
                    { "accounts_refreshed", "Odświeżono konta." },
                    { "account_switched", "Przełączono aktywne konto Codex." },
                    { "login_launched", "Zaloguj się w oknie Codex — AgentDeck automatycznie doda konto do listy." },
                    { "account_removed", "Usunięto profil konta." },
                    { "rc_updated", "Zaktualizowano status Remote Control." },
                    { "rc_active", "Aktywny" },
                    { "rc_active_ide", "Aktywny (IDE)" },
                    { "rc_inactive", "Nieaktywny" },
                    { "rc_registered", "Zarejestrowany" },
                    { "rc_not_registered", "Niezarejestrowany" },
                    { "unavailable", "Niedostępny" },
                    { "rc_desktop_connected", "Połączono zdalny Codex Desktop" },
                    { "rc_daemon_running", "Demon remote-control działa" },
                    { "rc_codex_idle", "Demon remote-control jest zatrzymany." },
                    { "rc_instance", "Instancja" },
                    { "rc_agy_idle", "Demon nie jest zarejestrowany." },
                    { "local_relay_running", "Lokalny przekaźnik działa na 127.0.0.1:{0}" },
                    { "local_relay_stopped", "Lokalny przekaźnik zatrzymany" },
                    { "local_relay_started", "Uruchomiono lokalny przekaźnik na porcie 8793." },
                    { "lang_saved", "Zapisano język." },
                    { "lang_saved_synced", "Zapisano język i zsynchronizowano ze Stickiem." },
                    { "settings_saved", "Zapisano ustawienia." },
                    { "stick_paired", "StickS3 został sparowany." },
                    { "select_stick_enter_code", "Wybierz Stick i wpisz kod z ekranu." },
                    { "no_sticks_found", "Brak Sticków w tej sieci." },
                    { "select_paired_stick", "Wybierz sparowany Stick." },
                    { "pairing_removed", "Parowanie usunięte z PC." },
                    { "stick_usb", "Stick przez USB" },
                    { "stick_remote", "Stick przez internet" },
                    { "stick_wifi", "Stick przez Wi-Fi" },
                    { "bridge_ready", "Mostek gotowy" },
                    { "bridge_offline", "Mostek niedostępny" },
                    { "waiting_stick", "Oczekiwanie na StickS3" },
                    { "stick_no_wifi", "Stick bez Wi-Fi" },
                    { "internet", "Internet" },
                    { "code_active", "Kod aktywny" },
                    { "state_queued", "W kolejce" },
                    { "state_running", "Pracuje" },
                    { "state_finished", "Ukończono" },
                    { "state_cancelled", "Zatrzymano" },
                    { "state_limit", "Limit konta" },
                    { "state_error", "Błąd" },
                    { "reading_session", "Odczytuję sesję..." },
                    { "select_session_prompt", "Wybierz sesję, aby zobaczyć użycia narzędzi, subagentów, komendy i wynik." },
                    { "full_answer_in_file", "Pełna odpowiedź jest w pliku." },
                    { "connect_stick_first", "Podłącz Stick." },
                    { "select_network", "Wybierz sieć z listy" },
                    { "select_network_err", "Wybierz sieć." },
                    { "password_length_err", "Wpisz hasło od 8 do 63 bajtów." },
                    { "stick_joining_wifi", "Stick łączy się z siecią..." },
                    { "reading_networks", "Odczytuję sieci..." },
                    { "secured", "Hasło" },
                    { "open", "Otwarta" },
                    { "no_networks_found", "Stick nie znalazł sieci." },
                    { "scan_timeout", "Brak odpowiedzi na skan." },
                    { "wifi_connected", "Stick połączył się z siecią." },
                    { "check_stick_screen", "Sprawdź stan sieci na ekranie Sticka." },
                    { "relay_saved", "Zapisano na PC. Przekaż adres na Stick." },
                    { "pair_for_channels", "Sparuj Stick, aby utworzyć kanały." },
                    { "channels_saved", "Zapisano kanały dla serwera przekaźnika." },
                    { "relay_reachable", "Stick odpowiada przez przekaźnik." },
                    { "relay_unreachable", "Brak odpowiedzi przez przekaźnik. Sprawdź adres i Wi-Fi." },
                    { "feedback_title", "Wyślij zgłoszenie na GitHub przez Sentry.io" },
                    { "feedback_desc", "Wyślij Bug Report lub Feature Request przez Sentry.io zintegrowane z github.com/natekk8/agentdeck." },
                    { "feedback_kind", "Rodzaj zgłoszenia" },
                    { "cat_bug", "Bug Report" },
                    { "cat_feature", "Feature Request" },
                    { "cat_other", "Inne" },
                    { "report_title_label", "Tytuł" },
                    { "report_contact_label", "Twój nick @github lub e-mail (opcjonalnie)" },
                    { "report_body_label", "Opis (kroki odtworzenia błędu lub szczegóły pomysłu)" },
                    { "report_logs_label", "Dołącz zanonimizowane logi" },
                    { "report_logs_desc", "Wysyła końcówkę logów mostka bez prywatnych nazw użytkownika." },
                    { "btn_send_report", "Wyślij zgłoszenie" },
                    { "sending_report", "Wysyłam zgłoszenie do Sentry.io..." },
                    { "report_sent_fmt", "Wysłano! Identyfikator Sentry: {0}" },
                    { "stick_fw_title", "Wymagana aktualizacja StickS3 (v{0})" },
                    { "stick_fw_title_ver", "Aktualizacja StickS3: v{0} -> v{1}" },
                    { "stick_fw_usb_sub", "Podłącz M5Stick S3 kablem USB-C, aby zaktualizować firmware do v{0}." },
                    { "stick_fw_ready_sub", "Wykryto StickS3 na USB. Kliknij, aby wgrać firmware v{0} jednym kliknięciem." },
                    { "stick_fw_btn", "Zaktualizuj StickS3" },
                    { "stick_fw_flashing", "Wgrywam firmware StickS3 przez USB..." },
                    { "stick_fw_flashed_fmt", "Zaktualizowano StickS3 do wersji v{0}." },
                    { "btn_flash_stick", "Wgraj firmware (USB)" },
                    { "after_update_title", "Zaktualizowano do AgentDeck v2.0" },
                    { "after_update_sub", "Wszystko jest gotowe do pracy na Twoim PC." },
                    { "after_update_notes", "• Nowy styl Obsidian Glass Studio + przeciąganie kolejności promptów na StickS3\n• Ciągi promptów (automatycznie jeden po drugim) i wyłączanie PC po pracy nawet po odłączeniu Sticka\n• Pełna integracja z Antigravity i Codex na żywo (komendy, narzędzia, subagenty, przeglądarka)" },
                    { "after_update_usb_title", "Firmware StickS3 (v2.0.0)" },
                    { "after_update_usb_hint", "Podłącz M5Stick S3 kablem USB-C, aby wgrać najnowszą wersję AgentDeck v2.0.0." },
                    { "after_update_btn", "Przejdź do AgentDeck" }
                }
            }
        };
    }
}
