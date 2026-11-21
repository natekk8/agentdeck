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
    readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
    readonly string[] sections = { "Home", "Prompts", "Sessions", "Accounts", "Devices", "Wifi", "Remote", "Feedback", "Settings" };
    readonly SolidColorBrush mint = Brush("#34D399");
    readonly SolidColorBrush blue = Brush("#60A5FA");
    readonly SolidColorBrush accent = Brush("#5F8BFF");
    readonly SolidColorBrush amber = Brush("#FBBF24");
    readonly SolidColorBrush coral = Brush("#FB7185");
    readonly SolidColorBrush muted = Brush("#8E9BB3");
    readonly SolidColorBrush textBrush = Brush("#F4F7FB");

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
    string appVersion = "";
    DateTime scanUntil, wifiUntil, loginWatchUntil = DateTime.MinValue;
    int knownCodexCount = -1;
    bool ticking;
    Record loadedPrompt = new Record(), linkSnapshot = new Record(), lastDevicesResult = new Record();

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

        // Custom Apple Window Chrome controls
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
            box.CornerRadius = new CornerRadius(max ? 0 : 12);
            box.BorderThickness = new Thickness(max ? 0 : 1);
            var maxIcon = C<System.Windows.Shapes.Path>("WinMaxIcon");
            if (maxIcon != null)
            {
                maxIcon.Data = Geometry.Parse(max
                    ? "M3,1.5 H9.5 V8 M1.5,3.5 H8 V9.5 H1.5 Z"
                    : "M1.5,1.5 H9.5 V9.5 H1.5 Z");
            }
        };

        foreach (var name in sections)
        {
            var page = name;
            C<Button>("Nav" + name).Click += (sender, e) => Navigate(page);
        }

        Button("FilterAll", () => SetProjectFilter(""));
        Button("FilterAgy", () => SetProjectFilter("agy"));
        Button("FilterCodex", () => SetProjectFilter("codex"));

        C<ListBox>("ProjectList").SelectionChanged += async (sender, e) => await ReadPrompts();
        C<ListBox>("PromptList").SelectionChanged += (sender, e) =>
        {
            var prompt = Selection("PromptList");
            if (prompt.Count != 0) FillPrompt(prompt);
        };
        C<ListBox>("SessionList").SelectionChanged += async (sender, e) => await ReadSession();
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
        Action("DeleteSessionBtn", "deleting_session", DeleteSelectedSession);
        Action("ClearSessionsBtn", "clearing_sessions", ClearFinishedSessions);

        C<ListBox>("CodexAccountList").AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnCodexListButtonClick));

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

        Button("NewPrompt", () => { C<ListBox>("PromptList").SelectedIndex = -1; FillPrompt(new Record()); });
        Action("SavePrompt", "saving_prompt", SavePrompt);
        Action("DeletePrompt", "deleting_prompt", DeletePrompt);
        Action("NewProjectBtn", "creating_project", NewProject);
        Action("RenameProjectBtn", "renaming_project", RenameProject);
        Action("AddProject", "select_project_folder", AddProject);
        Action("BindProject", "bind_agy_title", BindProject);
        Action("ShowProject", "sending_project", ShowProject);
        Button("UseProject", UseStickProject);

        Action("LoginCodexAccount", "launching_login", LoginCodexAccount);
        Action("RefreshAccountsBtn", "loading_accounts", () => RefreshAccounts(false));
        Action("RefreshAgyBtn", "loading_accounts", () => RefreshAccounts(true));

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
        Action("ScanWifi", "scanning_wifi", ScanWifi);
        Action("JoinWifi", "sending_wifi", JoinWifi);

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

        Action("SaveLanguageBtn", "applying_language", ApplySelectedLanguage);
        Button("BrowseProjectsRoot", BrowseProjectsRoot);
        Action("SaveProjectsRoot", "saving_settings", SaveProjectsRoot);
        Action("CheckUpdateBtn", "checking_updates", () => CheckForUpdateAsync(false));
        Button("OpenWizardBtn", () => ShowOnboarding(1));
        Button("OpenDataFolderBtn", () => OpenFolder(dataDir));
        Button("OpenAppFolderBtn", () => OpenFolder(appDir));

        Button("ReportCatBug", () => SetReportKind("bug"));
        Button("ReportCatFeature", () => SetReportKind("feature"));
        Button("ReportCatOther", () => SetReportKind("other"));
        Action("SendReportBtn", "sending_report", SubmitFeedbackReport);

        C<Button>("UpdateActionBtn").Click += async (s, e) => await HandleUpdateBannerClick();
        Button("UpdateDismissBtn", HideUpdateBanner);
        Action("StickFwUpdateBtn", "stick_fw_flashing", FlashStickFirmware);
        Action("FlashStickBtn", "stick_fw_flashing", FlashStickFirmware);
        Action("HomeFlashUsbBtn", "stick_fw_flashing", FlashStickFirmware);
        Action("HomePairUsbBtn", "pairing_usb", OnboardingPairUsb);
        Action("HomeSendBtn", "sending_home_prompt", SendHomePrompt);
        Button("HomeOpenSessionsBtn", () => Navigate("Sessions"));
        Action("RefreshIdeBtn", "checking_ides", () => RefreshIdeStatus(true));
        Action("InstallAgyBtn", "installing_agy", () => InstallIde("agy"));
        Action("InstallCodexBtn", "installing_codex", () => InstallIde("codex"));
        Action("OnbFlashUsbBtn", "stick_fw_flashing", FlashStickFirmware);
        Action("AfterUpdateFlashBtn", "stick_fw_flashing", FlashStickFirmware);
        Button("StickFwDismissBtn", () => { stickFwDismissed = true; C<Border>("StickFwBanner").Visibility = Visibility.Collapsed; });
        C<Button>("AfterUpdateCloseBtn").Click += async (s, e) => await CloseAfterUpdate();

        C<CheckBox>("AutostartToggle").Checked += async (s, e) => await OnAutostartToggled(true);
        C<CheckBox>("AutostartToggle").Unchecked += async (s, e) => await OnAutostartToggled(false);

        // Onboarding controls
        C<Button>("OnbLangEnBtn").Click += async (s, e) => await PickOnboardingLanguage("en");
        C<Button>("OnbLangPlBtn").Click += async (s, e) => await PickOnboardingLanguage("pl");
        Button("OnbBackBtn", () => { if (onboardingStep > 1) ShowOnboarding(onboardingStep - 1); });
        Action("OnbPairUsbBtn", "pairing_usb", OnboardingPairUsb);
        C<Button>("OnbNextBtn").Click += async (s, e) => await AdvanceOnboarding();
        C<Button>("OnbSkipBtn").Click += async (s, e) => await FinishOnboarding();

        ApplyLanguage();
        UpdateFilterButtons();
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
            double maxH = Math.Max(600, SystemParameters.WorkArea.Height - 24);
            double maxW = Math.Max(960, SystemParameters.WorkArea.Width - 24);
            if (window.Height > maxH) window.Height = maxH;
            if (window.Width > maxW) window.Width = maxW;
            StartAmbientAnimations();
            AnimateEntrance(C<FrameworkElement>("MainContainer"));
            ApplyLanguage();
            UpdateFilterButtons();
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
            try
            {
                window.Icon = BitmapFrame.Create(new Uri(path, UriKind.Absolute));
                return;
            }
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
    static SolidColorBrush Brush(string value) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); }
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
            BadgeBg = bg ?? Brush("#171E30");
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
        var api = new JavaScriptSerializer { MaxJsonLength = 1000000 };
        var data = new Record { { "op", op } };
        for (int i = 0; i < fields.Length; i += 2) data.Add((string)fields[i], fields[i + 1]);
        var settings = api.Deserialize<Record>(File.ReadAllText(marker));
        var key = ProtectedData.Unprotect(Convert.FromBase64String(Str(settings, "token")), null, DataProtectionScope.CurrentUser);
        using (var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:" + Str(settings, "port") + "/desktop"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Encoding.UTF8.GetString(key));
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

    void Navigate(string name)
    {
        section = name;
        foreach (var page in sections)
        {
            var button = C<Button>("Nav" + page);
            var active = page == name;
            button.Background = active ? Brush("#172444") : Brushes.Transparent;
            button.BorderBrush = active ? Brush("#33529E") : Brushes.Transparent;
            C<TextBlock>("Nav" + page + "Text").Foreground = active ? textBrush : muted;
            C<FrameworkElement>(page).Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateHeading();
        var entry = C<FrameworkElement>(name);
        if (SystemParameters.ClientAreaAnimation && entry != null)
        {
            var time = TimeSpan.FromMilliseconds(220);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var transform = new TranslateTransform();
            entry.RenderTransform = transform;
            entry.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.25, 1, time) { EasingFunction = ease });
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, time) { EasingFunction = ease });
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
            var anim1 = new DoubleAnimation(0.28, 0.58, TimeSpan.FromSeconds(4.2))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            orb1.BeginAnimation(UIElement.OpacityProperty, anim1);
        }
        if (orb2 != null)
        {
            var anim2 = new DoubleAnimation(0.22, 0.48, TimeSpan.FromSeconds(5.4))
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
        else
        {
            rot.BeginAnimation(RotateTransform.AngleProperty, null);
        }
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
        btn.Background = active ? Brush("#172444") : Brush("#171E30");
        btn.BorderBrush = active ? Brush("#4162B5") : Brush("#283552");
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
                return new Choice(row, Str(row, "label"), Str(row, "path"), "GLOBAL", Brush("#172444"), accent);
            var provs = Items(row, "providers").Select(p => Convert.ToString(p)).ToArray();
            string badge = Flag(row, "created_on_stick") ? "NEW" :
                (provs.Contains("codex") && provs.Contains("agy")) ? "BOTH" :
                provs.Contains("codex") ? "CODEX" :
                provs.Contains("agy") ? "AGY" : "";
            var bg = badge == "AGY" ? Brush("#132642") : badge == "CODEX" ? Brush("#0F2D24") :
                badge == "NEW" ? Brush("#332710") : Brush("#1C2438");
            var fg = badge == "AGY" ? blue : badge == "CODEX" ? mint :
                badge == "NEW" ? amber : muted;
            return new Choice(row, Str(row, "label"), Str(row, "path"), badge, bg, fg);
        }, selected);
        if (C<ListBox>("ProjectList").SelectedIndex < 0 && C<ListBox>("ProjectList").Items.Count > 0)
            C<ListBox>("ProjectList").SelectedIndex = 0;

        var realProjects = Rows(result, "items");
        var prevHomeProj = Str(Selected("HomeProject"), "id");
        Fill("HomeProject", realProjects, row => Str(row, "label") + "  ·  " + Str(row, "path"), prevHomeProj);
    }

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
        Fill("HomeProvider", installedList, r => Str(r, "label"), prevProv);
        C<Button>("HomeSendBtn").IsEnabled = installedList.Count > 0;
    }

    void UpdateIdeRow(string prefix, Record info)
    {
        bool installed = Flag(info, "installed");
        string ver = Str(info, "version");
        string path = Str(info, "path");
        C<TextBlock>(prefix + "IdeStatus").Text = installed ? L("ide_installed") : L("ide_missing");
        C<TextBlock>(prefix + "IdeStatus").Foreground = installed ? mint : amber;
        C<Border>(prefix + "IdeBadge").Background = installed ? Brush("#0F2D24") : Brush("#2E2210");
        C<Border>(prefix + "IdeBadge").BorderBrush = installed ? Brush("#1F5B4A") : Brush("#6E4E18");
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

    async Task SendHomePrompt()
    {
        var prov = Str(Selected("HomeProvider"), "id");
        var proj = Str(Selected("HomeProject"), "id");
        var text = C<TextBox>("HomePromptText").Text.Trim();
        var modeItem = C<ComboBox>("HomeMode").SelectedItem as ComboBoxItem;
        var mode = modeItem != null && modeItem.Tag != null ? modeItem.Tag.ToString() : "read";
        if (prov.Length == 0) throw new InvalidOperationException(L("err_no_ide_installed"));
        if (proj.Length == 0) throw new InvalidOperationException(L("err_select_project"));
        if (text.Length == 0) throw new InvalidOperationException(L("err_enter_prompt"));
        await Api("start_prompt", "provider", prov, "project", proj, "text", text, "mode", mode);
        C<TextBox>("HomePromptText").Clear();
        sessionSignature = "";
        Note(L("prompt_sent"));
        Navigate("Sessions");
        await RefreshState();
    }

    sealed class PromptTemplate
    {
        public string Label;
        public string Description;
        public string Text;
        public PromptTemplate(string label, string description, string text)
        {
            Label = label;
            Description = description;
            Text = text;
        }
    }

    static readonly PromptTemplate[] TemplatesEn = new PromptTemplate[]
    {
        new PromptTemplate(
            "Architecture Audit",
            "Maps project structure, entry points, dependencies, and technical debt.",
            "Inspect the repository structure, entry points, and core modules. Summarize the architecture, key data flows, dependencies, and the top 3 structural improvements to prioritize."
        ),
        new PromptTemplate(
            "Bug & Risk Hunter",
            "Hunts edge cases, race conditions, security flaws, and silent failures.",
            "Audit the codebase for bugs, edge cases, race conditions, input validation gaps, and error handling issues. List concrete file paths, root causes, and recommended fixes."
        ),
        new PromptTemplate(
            "Feature Blueprint",
            "Designs and builds a new feature end-to-end with tests and verification.",
            "Analyze the codebase conventions and implement the requested feature cleanly across all required modules. Keep changes modular, avoid duplication, and verify with tests or build checks."
        ),
        new PromptTemplate(
            "Diff Code Review",
            "Reviews recent changes for regressions, cleanliness, and missing tests.",
            "Review the most recently modified files and git changes. Check for regressions, subtle logic bugs, naming consistency, and missing test coverage."
        ),
        new PromptTemplate(
            "Performance Pass",
            "Finds hot paths, redundant I/O, slow queries, and UI/memory bottlenecks.",
            "Profile the code statically for performance bottlenecks, unnecessary allocations, blocking I/O, and N+1 loops. Propose or apply safe high-impact optimizations."
        ),
        new PromptTemplate(
            "Test Coverage Check",
            "Inspects test setup, finds untested paths, and writes missing unit tests.",
            "Check the test suite and build pipeline. Identify critical untested code paths and edge cases, then add reliable unit tests to cover them."
        )
    };

    static readonly PromptTemplate[] TemplatesPl = new PromptTemplate[]
    {
        new PromptTemplate(
            "Przegląd architektury",
            "Mapuje strukturę projektu, główne punkty wejścia, zależności i dług techniczny.",
            "Przeanalizuj strukturę repozytorium, punkty wejścia i główne moduły. Podsumuj architekturę, przepływ danych, kluczowe zależności oraz 3 najważniejsze usprawnienia techniczne."
        ),
        new PromptTemplate(
            "Analiza błędów i ryzyk",
            "Wyszukuje błędy brzegowe, wyścigi, luki bezpieczeństwa i ciche wyjątki.",
            "Przeszukaj kod pod kątem błędów logicznych, przypadków brzegowych, wyścigów, brakującej walidacji oraz problemów z obsługą błędów. Wskaż konkretne pliki, przyczyny i poprawki."
        ),
        new PromptTemplate(
            "Projekt i wdrożenie funkcji",
            "Projektuje oraz wdraża nową funkcję zgodnie z konwencjami projektu i testami.",
            "Zapoznaj się z architekturą i konwencjami projektu, a następnie zaprojektuj i wdróż spójnie nową funkcjonalność. Zadbaj o czysty podział kodu i zweryfikuj wynik testami lub kompilacją."
        ),
        new PromptTemplate(
            "Przegląd zmian (Code Review)",
            "Sprawdza ostatnie modyfikacje pod kątem regresji, czytelności i testów.",
            "Przejrzyj ostatnio zmodyfikowane pliki oraz zmiany w Git. Wskaż potencjalne regresje, ukryte błędy, niespójności oraz brakujące testy."
        ),
        new PromptTemplate(
            "Optymalizacja wydajności",
            "Wykrywa wąskie gardła, blokujące operacje I/O, wolne pętle i zużycie pamięci.",
            "Przeanalizuj kod pod kątem wąskich gardeł wydajnościowych, zbędnych alokacji, blokujących operacji sieciowych/dyskowych oraz nadmiarowych zapytań. Zaproponuj lub wdróż bezpieczne optymalizacje."
        ),
        new PromptTemplate(
            "Audyt testów i jakości",
            "Ocenia pokrycie testami, znajduje luki i dopisuje brakujące testy jednostkowe.",
            "Sprawdź konfigurację testów i budowania projektu. Wskaż najważniejsze nieprzetestowane ścieżki oraz dopisz niezawodne testy jednostkowe dla kluczowych przypadków."
        )
    };

    void PopulateTemplateChips()
    {
        var panel = C<WrapPanel>("TemplateChips");
        panel.Children.Clear();
        var list = lang == "pl" ? TemplatesPl : TemplatesEn;
        foreach (var tpl in list)
        {
            var captured = tpl;
            var btn = new Button
            {
                Content = captured.Label,
                ToolTip = captured.Description,
                Style = (Style)window.FindResource("PillBtn"),
                Margin = new Thickness(0, 0, 6, 6)
            };
            btn.Click += (s, e) =>
            {
                C<ListBox>("PromptList").SelectedIndex = -1;
                loadedPrompt = new Record();
                C<TextBox>("PromptTitle").Text = captured.Label;
                C<TextBox>("PromptDescription").Text = captured.Description;
                C<TextBox>("PromptText").Text = captured.Text;
                Note(captured.Description);
            };
            panel.Children.Add(btn);
        }
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
            FillPrompt(new Record());
            FillChoices("PromptList", Rows(result, "items").OrderBy(row => Str(row, "label")), row =>
            {
                var p = Str(row, "provider");
                var tag = p == "codex" ? "Codex" : p == "agy" ? "AGY" : L("both_short");
                return new Choice(row, Str(row, "label"), Str(row, "description"), tag,
                    p == "codex" ? Brush("#0F2D24") : p == "agy" ? Brush("#132642") : Brush("#171E30"),
                    p == "codex" ? mint : p == "agy" ? blue : muted);
            }, selected);
        }
        catch (Exception error) { Note(error.Message, true); }
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

    Window DarkModal(string title, double width)
    {
        return new Window
        {
            Owner = window,
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = Brush("#111623"),
            Foreground = textBrush,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = 13.5
        };
    }

    async Task NewProject()
    {
        var dialog = DarkModal(L("new_project_title"), 440);
        var panel = new StackPanel { Margin = new Thickness(22) };
        var label = new TextBlock { Text = L("new_project_name_hint"), Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        var input = new TextBox { MaxLength = 64, Background = Brush("#090D16"), Foreground = textBrush, BorderBrush = Brush("#283552"), Padding = new Thickness(11, 9, 11, 9), Margin = new Thickness(0, 0, 0, 14) };
        var ideLabel = new TextBlock { Text = L("new_project_ide"), Foreground = muted, Margin = new Thickness(0, 0, 0, 6) };
        var ideBox = new ComboBox { Margin = new Thickness(0, 0, 0, 18) };
        ideBox.Items.Add(new ComboBoxItem { Content = L("both_ides"), Tag = "" });
        ideBox.Items.Add(new ComboBoxItem { Content = "Codex", Tag = "codex" });
        ideBox.Items.Add(new ComboBoxItem { Content = "Antigravity", Tag = "agy" });
        ideBox.SelectedIndex = projectFilter == "codex" ? 1 : projectFilter == "agy" ? 2 : 0;

        var rowBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = L("cancel"), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(15, 8, 15, 8) };
        var create = new Button { Content = L("create"), IsDefault = true, Style = (Style)window.FindResource("PrimaryBtn"), Margin = new Thickness(0) };
        cancel.Click += (s, e) => dialog.DialogResult = false;
        create.Click += (s, e) => dialog.DialogResult = true;
        rowBtns.Children.Add(cancel);
        rowBtns.Children.Add(create);

        panel.Children.Add(label);
        panel.Children.Add(input);
        panel.Children.Add(ideLabel);
        panel.Children.Add(ideBox);
        panel.Children.Add(rowBtns);
        dialog.Content = panel;
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

        var dialog = DarkModal(L("rename_project_title"), 420);
        var panel = new StackPanel { Margin = new Thickness(22) };
        var label = new TextBlock { Text = L("rename_project_label"), Foreground = muted, Margin = new Thickness(0, 0, 0, 8) };
        var input = new TextBox { Text = Str(row, "label"), MaxLength = 64, Background = Brush("#090D16"), Foreground = textBrush, BorderBrush = Brush("#283552"), Padding = new Thickness(11, 9, 11, 9), Margin = new Thickness(0, 0, 0, 16) };
        input.SelectAll();
        var save = new Button { Content = L("save"), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Style = (Style)window.FindResource("PrimaryBtn"), Margin = new Thickness(0) };
        save.Click += (s, e) => dialog.DialogResult = true;
        panel.Children.Add(label);
        panel.Children.Add(input);
        panel.Children.Add(save);
        dialog.Content = panel;
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
        var dialog = DarkModal(L("bind_agy_title"), 420);
        var content = new StackPanel { Margin = new Thickness(22) };
        var input = new TextBox { Text = Str(row, "agy_project"), MaxLength = 120, Margin = new Thickness(0, 8, 0, 16), Background = Brush("#090D16"), Foreground = textBrush, BorderBrush = Brush("#283552"), Padding = new Thickness(11, 9, 11, 9) };
        var save = new Button { Content = L("save"), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Style = (Style)window.FindResource("PrimaryBtn"), Margin = new Thickness(0) };
        save.Click += (sender, e) => dialog.DialogResult = true;
        content.Children.Add(new TextBlock { Text = L("agy_project_id_label"), Foreground = muted });
        content.Children.Add(input);
        content.Children.Add(save);
        dialog.Content = content;
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
                Brush("#0F2D24"),
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
            return new Choice(row, Str(row, "label"), Str(row, "email"), isAct ? L("active_badge") : "", Brush("#12233E"), blue);
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

    // Auto-Updater Flow
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
        else if (!silent)
        {
            Note(L("no_updates_found"));
        }
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
            else
            {
                C<TextBlock>("UpdateBannerSub").Text = string.Format(L("update_dl_mb"), dlMb);
            }
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

    // Onboarding Flow
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
        enBtn.Background = lang == "en" ? Brush("#172444") : Brush("#0B0F1A");
        enBtn.BorderBrush = lang == "en" ? accent : Brush("#26334E");
        plBtn.Background = lang == "pl" ? Brush("#172444") : Brush("#0B0F1A");
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
        if (onboardingStep == 1)
        {
            ShowOnboarding(2);
            return;
        }
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
        if (fw.Length == 0) fw = "1.0.0";
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
            FillChoices("DeviceList", found, row => new Choice(row, DeviceTitle(row), Str(row, "address"), Flag(row, "pairing") ? L("code_active") : "", Brush("#172444"), accent), Str(ready, "id"));
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
        SolidColorBrush bg = (charging || bat >= 45) ? Brush("#0F2D24") : (bat >= 20 ? Brush("#2E2210") : Brush("#2E131E"));
        SolidColorBrush bd = (charging || bat >= 45) ? Brush("#1F5B4A") : (bat >= 20 ? Brush("#5C4319") : Brush("#5C2032"));
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
        if (expVer.Length == 0) expVer = "1.0.0";
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
                    var bg = bat >= 45 || Flag(snap, "charging") ? Brush("#0F2D24") : bat >= 20 ? Brush("#2E2210") : Brush("#2E131E");
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

            if (section == "Sessions")
            {
                var sessions = Rows(await Api("recent_sessions"), "items");
                var states = string.Join("|", sessions.Select(row => Str(row, "id") + Str(row, "state")));
                if (states != sessionSignature)
                {
                    sessionSignature = states;
                    var selectedId = Str(Selected("SessionList"), "id");
                    FillChoices("SessionList", sessions, row =>
                    {
                        var st = Str(row, "state");
                        var fg = st == "finished" ? mint : st == "running" ? blue : (st == "error" || st == "orphaned") ? coral : amber;
                        var bg = st == "finished" ? Brush("#0F2D24") : st == "running" ? Brush("#12233E") : (st == "error" || st == "orphaned") ? Brush("#2E131E") : Brush("#2E2210");
                        return new Choice(row, Str(row, "label"), Str(row, "detail"), SessionState(st), bg, fg);
                    }, selectedId.Length > 0 ? selectedId : (sessions.Length > 0 ? Str(sessions[0], "id") : ""));
                    await ReadSession();
                }
                else
                {
                    var activeId = Str(Selected("SessionList"), "id");
                    if (activeId.Length > 0 && sessions.Any(s => Str(s, "id") == activeId &&
                        (Str(s, "state") == "running" || Str(s, "state") == "waiting_input")))
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

    async Task ReadSession()
    {
        var id = Str(Selected("SessionList"), "id");
        C<Button>("OpenConversation").IsEnabled = false;
        C<Button>("OpenAnswer").IsEnabled = false;
        C<Button>("DeleteSessionBtn").IsEnabled = id.Length > 0;
        C<Button>("OpenAnswer").Tag = null;
        C<Border>("QuestionPanel").Visibility = Visibility.Collapsed;
        C<TextBox>("SessionAnswer").Clear();
        C<TextBlock>("SessionLocation").Text = id.Length > 0 ? L("reading_session") : L("select_session_prompt");
        if (id.Length == 0) return;
        try
        {
            var result = await Api("session_details", "job", id);
            if (id != Str(Selected("SessionList"), "id")) return;
            var provider = Str(result, "provider");
            var effort = Str(result, "effort");
            var heading = string.Join("  ·  ", new[] {
                provider == "agy" ? "Antigravity" : "Codex",
                Str(result, "model_label"), effort == "xhigh" ? "XHigh" : effort,
                SessionState(Str(result, "state"))
            }.Where(value => value.Length > 0));
            var title = Str(result, "conversation_title");
            if (provider == "agy" && Str(result, "conversation").Length > 0)
            {
                if (Flag(result, "cli_project")) heading += "\nProjects -> CLI Project";
                if (title.Length > 0) heading += "\nCtrl+K  ·  " + title;
                else heading += "\n" + L("untitled_conv");
            }
            if (Flag(result, "answer_truncated")) heading += "\n" + L("full_answer_in_file");
            C<TextBlock>("SessionLocation").Text = heading;
            C<TextBox>("SessionAnswer").Text = Display(result, "answer", Display(result, "preview", Str(result, "detail")));
            RenderSessionQuestion(id, result);
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
        C<TextBlock>("HomeQuickTitle").Text = L("home_quick_title");
        C<TextBlock>("HomeQuickDesc").Text = L("home_quick_desc");
        C<TextBlock>("HomeIdeLabel").Text = L("home_ide_label");
        C<TextBlock>("HomeModeLabel").Text = L("home_mode_label");
        C<ComboBoxItem>("HomeModeRead").Content = L("home_mode_read");
        C<ComboBoxItem>("HomeModeWrite").Content = L("home_mode_write");
        C<TextBlock>("HomeProjectLabel").Text = L("home_project_label");
        C<TextBlock>("HomePromptLabel").Text = L("home_prompt_label");
        C<Button>("HomeSendBtn").Content = L("btn_send_prompt");
        C<Button>("HomeOpenSessionsBtn").Content = L("btn_open_sessions");
        C<TextBlock>("HomeIdesTitle").Text = L("home_ides_title");
        C<TextBlock>("HomeIdesDesc").Text = L("home_ides_desc");
        C<Button>("RefreshIdeBtn").Content = L("btn_refresh");
        C<Button>("InstallAgyBtn").Content = L("btn_install");
        C<Button>("InstallCodexBtn").Content = L("btn_install");
        C<TextBlock>("HomeStickTitle").Text = L("home_stick_title");
        C<Button>("HomePairUsbBtn").Content = L("btn_pair_usb");
        C<Button>("HomeFlashUsbBtn").Content = L("btn_flash_stick");
        C<TextBlock>("NavSessionsText").Text = L("nav_sessions");
        C<TextBlock>("NavAccountsText").Text = L("nav_accounts");
        C<TextBlock>("NavDevicesText").Text = L("nav_devices");
        C<TextBlock>("NavWifiText").Text = L("nav_wifi");
        C<TextBlock>("NavRemoteText").Text = L("nav_remote");
        C<TextBlock>("NavFeedbackText").Text = L("nav_feedback");
        C<TextBlock>("NavSettingsText").Text = L("nav_settings");

        C<TextBlock>("ProjectsTitle").Text = L("projects_title");
        C<Button>("FilterAll").Content = L("filter_all");
        C<Button>("NewProjectBtn").Content = L("btn_new_project");
        C<Button>("RenameProjectBtn").Content = L("btn_rename");
        C<Button>("AddProject").Content = L("btn_add_folder");
        C<Button>("ShowProject").Content = L("btn_open_stick");
        C<Button>("UseProject").Content = L("btn_sync_stick");
        C<Button>("BindProject").Content = L("btn_bind_agy");
        C<TextBlock>("SuggestedTitle").Text = L("suggested_title");
        C<TextBlock>("PromptTitleLabel").Text = L("prompt_name");
        C<TextBlock>("PromptDescLabel").Text = L("prompt_desc");
        C<ComboBoxItem>("ProviderBoth").Content = L("both_ides");
        C<Button>("SavePrompt").Content = L("btn_save_prompt");
        C<Button>("NewPrompt").Content = L("btn_new_prompt");
        C<Button>("DeletePrompt").Content = L("btn_delete");
        PopulateTemplateChips();

        C<TextBlock>("SessionsTitle").Text = L("sessions_title");
        C<Button>("OpenConversation").Content = L("btn_open_agy_cli");
        C<Button>("OpenAnswer").Content = L("btn_open_answer");

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
        C<Button>("DeleteSessionBtn").Content = L("btn_delete_session");
        C<Button>("ClearSessionsBtn").Content = L("btn_clear_sessions");
        C<Button>("FlashStickBtn").Content = L("btn_flash_stick");
        C<Button>("OnbFlashUsbBtn").Content = L("btn_flash_stick");
        C<Button>("AfterUpdateFlashBtn").Content = L("btn_flash_stick");
        C<TextBlock>("AfterUpdateTitle").Text = L("after_update_title");
        C<TextBlock>("AfterUpdateSub").Text = L("after_update_sub");
        C<TextBlock>("AfterUpdateNotes").Text = L("after_update_notes");
        C<TextBlock>("AfterUpdateUsbTitle").Text = L("after_update_usb_title");
        C<TextBlock>("AfterUpdateUsbHint").Text = L("after_update_usb_hint");
        C<Button>("AfterUpdateCloseBtn").Content = L("after_update_btn");

        if (lastDevicesResult.Count > 0) UpdateStickFirmwareBanner(lastDevicesResult);
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
                    { "window_title", "AgentDeck (v1.1) | Remote AI IDE control in your pocket." },
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
                    { "sub_prompts", "IDE-synced projects, quick random workspaces, and global or per-project prompts" },
                    { "sub_sessions", "Inspect live running jobs, answer agent questions, and review completed output" },
                    { "sub_accounts", "Auto-synced Codex accounts with 1-click switching and live Antigravity subscription status" },
                    { "sub_devices", "Pair and monitor your M5Stick S3 battery & link over USB or Wi-Fi" },
                    { "sub_wifi", "Configure wireless networks directly on your StickS3" },
                    { "sub_remote", "Official Codex & Antigravity Remote Control + StickS3 away-from-home relay" },
                    { "sub_feedback", "Send Bug Reports & Feature Requests to GitHub via Sentry.io" },
                    { "sub_settings", "Autostart for Wake on LAN, language preferences, Sentry DSN, and updates" },
                    { "projects_title", "IDE Projects" },
                    { "filter_all", "All" },
                    { "btn_new_project", "+ New Project" },
                    { "btn_rename", "Rename" },
                    { "btn_add_folder", "Add Folder" },
                    { "btn_open_stick", "Open on Stick" },
                    { "btn_sync_stick", "Sync from Stick" },
                    { "btn_bind_agy", "Bind Antigravity ID" },
                    { "global_projects_title", "All Projects (Global)" },
                    { "global_projects_sub", "Available in every project on StickS3" },
                    { "global_hint", "Global prompts appear in all projects, including new random workspaces." },
                    { "select_project", "Select a project" },
                    { "both_ides", "Both IDEs" },
                    { "both_short", "Both" },
                    { "suggested_title", "Suggested templates (click to load):" },
                    { "prompt_name", "Prompt Name" },
                    { "prompt_desc", "Short description (what this prompt does)" },
                    { "state_waiting_input", "Question" },
                    { "sending_answer", "Sending answer to agent..." },
                    { "answer_sent", "Answer sent to agent." },
                    { "btn_save_prompt", "Save Prompt" },
                    { "btn_new_prompt", "Clear / New" },
                    { "btn_delete", "Delete" },
                    { "sessions_title", "Recent Stick Sessions" },
                    { "btn_open_agy_cli", "Open in Antigravity CLI" },
                    { "btn_open_answer", "Open Output File" },
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
                    { "settings_about_title", "Setup Wizard & Folders" },
                    { "settings_about_desc", "AgentDeck (v1 Preview) · Check GitHub Releases for updates, run the setup wizard, or open local folders." },
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
                    { "select_session_prompt", "Select a session to read its output." },
                    { "untitled_conv", "Untitled conversation. Open in Antigravity CLI." },
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
                    { "settings_gt_title", "Sentry.io Reporter DSN" },
                    { "settings_gt_desc", "Sentry.io Project Client Key (DSN) used by Bug Report & Feature Request submissions." },
                    { "btn_save_dsn", "Save DSN" },
                    { "configured", "Configured" },
                    { "not_configured", "Not configured" },
                    { "dsn_saved", "Sentry DSN saved." },
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
                    { "after_update_title", "Updated to AgentDeck (v1.1)" },
                    { "after_update_sub", "Everything is ready on your PC." },
                    { "after_update_notes", "• New Home dashboard with Quick Prompt launcher & 1-click AI IDE manager\n• Full Antigravity & Codex multi-subagent output without early cutoff\n• Dynamic StickS3 menu showing only installed IDEs & session deletion" },
                    { "after_update_usb_title", "StickS3 Firmware (v1.1.0)" },
                    { "after_update_usb_hint", "Plug in M5Stick S3 via USB-C to flash the latest AgentDeck v1.1 firmware in one click." },
                    { "after_update_btn", "Continue to AgentDeck" },
                    { "btn_delete_session", "Delete Session" },
                    { "btn_clear_sessions", "Clear Finished" },
                    { "deleting_session", "Deleting session..." },
                    { "clearing_sessions", "Clearing finished sessions..." },
                    { "session_deleted", "Session deleted." },
                    { "sessions_cleared_fmt", "Cleared {0} finished session(s)." },
                    { "btn_flash_stick", "Flash Firmware (USB)" },
                    { "sub_home", "Send prompts, manage Antigravity CLI & ChatGPT Codex, and connect StickS3" },
                    { "home_quick_title", "Quick Prompt" },
                    { "home_quick_desc", "Run a prompt directly from Windows using your installed AI IDE and watch progress live in Sessions." },
                    { "home_ide_label", "AI IDE" },
                    { "home_mode_label", "Mode" },
                    { "home_mode_read", "Read-only (Analysis)" },
                    { "home_mode_write", "Write (Edit files)" },
                    { "home_project_label", "Project" },
                    { "home_prompt_label", "Prompt" },
                    { "btn_send_prompt", "Send Prompt" },
                    { "btn_open_sessions", "Open Sessions" },
                    { "home_ides_title", "Installed AI IDEs" },
                    { "home_ides_desc", "Only installed IDEs appear on your StickS3. Install missing CLIs in one click." },
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
                    { "err_enter_prompt", "Enter a prompt first." }
                }
            },
            {
                "pl", new Dictionary<string, string>
                {
                    { "window_title", "AgentDeck (v1.1) | Zdalne sterowanie AI IDE w Twojej kieszeni." },
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
                    { "sub_prompts", "Projekty wczytane z wybranego IDE, szybkie losowe foldery i szablony promptów" },
                    { "sub_sessions", "Przeglądaj aktywne i ukończone zadania oraz odpowiadaj na pytania agentów" },
                    { "sub_accounts", "Automatycznie zapisane konta Codex (1-klik) oraz podgląd subskrypcji Antigravity" },
                    { "sub_devices", "Paruj StickS3 i sprawdzaj poziom baterii oraz połączenie USB / Wi-Fi" },
                    { "sub_wifi", "Konfiguruj sieci bezprzewodowe bezpośrednio na StickS3" },
                    { "sub_remote", "Natywne Remote Control Codex i Antigravity + szyfrowany przekaźnik StickS3" },
                    { "sub_feedback", "Wysyłaj Bug Reporty i Feature Requesty na GitHub przez Sentry.io" },
                    { "sub_settings", "Autostart pod Wake on LAN, język, konfiguracja Sentry.io i aktualizacje" },
                    { "projects_title", "Projekty z IDE" },
                    { "filter_all", "Wszystkie" },
                    { "btn_new_project", "+ Nowy projekt" },
                    { "btn_rename", "Zmień nazwę" },
                    { "btn_add_folder", "Dodaj folder" },
                    { "btn_open_stick", "Otwórz na Sticku" },
                    { "btn_sync_stick", "Wczytaj ze Sticka" },
                    { "btn_bind_agy", "Powiąż z Antigravity" },
                    { "global_projects_title", "Wszystkie projekty (globalne)" },
                    { "global_projects_sub", "Dostępne w każdym projekcie na StickS3" },
                    { "global_hint", "Prompty globalne są widoczne w każdym projekcie, także w losowych ze Sticka." },
                    { "select_project", "Wybierz projekt" },
                    { "both_ides", "Oba IDE" },
                    { "both_short", "Oba" },
                    { "suggested_title", "Proponowane szablony (kliknij, aby wczytać):" },
                    { "prompt_name", "Nazwa promptu" },
                    { "prompt_desc", "Krótki opis (co robi ten prompt)" },
                    { "state_waiting_input", "Pytanie" },
                    { "sending_answer", "Wysyłam odpowiedź do agenta..." },
                    { "answer_sent", "Przekazano odpowiedź do agenta." },
                    { "btn_save_prompt", "Zapisz prompt" },
                    { "btn_new_prompt", "Nowy / wyczyść" },
                    { "btn_delete", "Usuń" },
                    { "sessions_title", "Ostatnie sesje ze Sticka" },
                    { "btn_open_agy_cli", "Otwórz w Antigravity CLI" },
                    { "btn_open_answer", "Otwórz plik odpowiedzi" },
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
                    { "settings_about_title", "Kreator konfiguracji i foldery" },
                    { "settings_about_desc", "AgentDeck (v1 Preview) · Sprawdzaj aktualizacje na GitHub Releases, uruchom kreator lub otwórz lokalne foldery." },
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
                    { "select_session_prompt", "Wybierz sesję, aby przeczytać odpowiedź." },
                    { "untitled_conv", "Rozmowa bez tytułu. Otwórz w Antigravity CLI." },
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
                    { "settings_gt_title", "Sentry.io DSN dla zgłoszeń" },
                    { "settings_gt_desc", "Klucz projektu (Client Key / DSN) z Sentry.io do wysyłania Bug Reportów i Feature Requestów." },
                    { "btn_save_dsn", "Zapisz DSN" },
                    { "configured", "Skonfigurowano" },
                    { "not_configured", "Brak klucza" },
                    { "dsn_saved", "Zapisano Sentry DSN." },
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
                    { "after_update_title", "Zaktualizowano do AgentDeck (v1.1)" },
                    { "after_update_sub", "Wszystko jest gotowe do pracy na Twoim PC." },
                    { "after_update_notes", "• Nowa Strona główna: szybkie wysyłanie promptów i zarządzanie IDE jednym kliknięciem\n• Pełne raporty z Antigravity i Codex (czeka na wszystkie subagenty bez ucinania)\n• StickS3 pokazuje tylko zainstalowane IDE + usuwanie sesji i wgrywanie firmware przez USB" },
                    { "after_update_usb_title", "Firmware StickS3 (v1.1.0)" },
                    { "after_update_usb_hint", "Podłącz M5Stick S3 kablem USB-C, aby wgrać najnowszą wersję AgentDeck v1.1 na Sticka." },
                    { "after_update_btn", "Przejdź do AgentDeck" },
                    { "btn_delete_session", "Usuń sesję" },
                    { "btn_clear_sessions", "Wyczyść ukończone" },
                    { "deleting_session", "Usuwam sesję..." },
                    { "clearing_sessions", "Czyszczę ukończone sesje..." },
                    { "session_deleted", "Usunięto sesję." },
                    { "sessions_cleared_fmt", "Usunięto ukończone sesje: {0}." },
                    { "btn_flash_stick", "Wgraj firmware (USB)" },
                    { "sub_home", "Wysyłaj prompty, zarządzaj Antigravity CLI i ChatGPT Codex oraz połącz StickS3" },
                    { "home_quick_title", "Szybki prompt" },
                    { "home_quick_desc", "Uruchom prompt bezpośrednio z Windows na wybranym zainstalowanym IDE i podglądaj postęp w Sesjach." },
                    { "home_ide_label", "AI IDE" },
                    { "home_mode_label", "Tryb" },
                    { "home_mode_read", "Tylko odczyt (Analiza)" },
                    { "home_mode_write", "Zapis (Edycja plików)" },
                    { "home_project_label", "Projekt" },
                    { "home_prompt_label", "Treść promptu" },
                    { "btn_send_prompt", "Wyślij prompt" },
                    { "btn_open_sessions", "Otwórz sesje" },
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
                    { "home_stick_title", "Połączenie ze StickS3" },
                    { "home_stick_usb_ok", "StickS3 podłączony przez USB-C. Możesz go sparować lub wgrać firmware." },
                    { "home_stick_usb_wait", "Podłącz M5Stick S3 kablem USB-C, aby sparować lub wgrać najnowszy firmware." },
                    { "sending_home_prompt", "Uruchamiam sesję promptu..." },
                    { "prompt_sent", "Prompt uruchomiony! Podgląd na żywo w Sesjach." },
                    { "err_no_ide_installed", "Najpierw zainstaluj Antigravity CLI lub ChatGPT Codex." },
                    { "err_select_project", "Wybierz projekt z listy." },
                    { "err_enter_prompt", "Wpisz treść promptu." }
                }
            }
        };
    }
}
