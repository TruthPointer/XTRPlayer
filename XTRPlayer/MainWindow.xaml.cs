using FlyleafLib;
using FlyleafLib.Controls.WPF;
using FlyleafLib.MediaPlayer;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace XTRPlayer
{
    // TODO: Popup Menu Playlist will not resize the size?
    //       Add Play Next/Prev for Playlists (Page Up/Down?) this goes down to Player

    /// <summary>
    /// <para>FlyleafPlayer Sample</para>
    /// <para>A stand-alone Overlay which uses a customization of FlyleafME control</para>
    /// </summary>
    public partial class MainWindow : Window
    {
        const int ACTIVITY_TIMEOUT = 3500;
        public event PropertyChangedEventHandler PropertyChanged;
        //public static string FlyleafLibVer => "FlyleafLib v" + System.Reflection.Assembly.GetAssembly(typeof(Engine)).GetName().Version;
        public static string FlyleafLibVer => "新唐人中国频道" + GetApplicationVersion() + PatchProxyInfo();
        //public static string FlyleafLibVer = FlyleafLibVer1;

        /////////////////////////////////////////////////////
        ///1.内部类
        #region
        public class XtrUrl
        {
            public string QualityName;
            public string Url;
            public XtrUrl(string qualityName, string url)
            {
                QualityName = qualityName;
                Url = url;
            }
        }
        public class Settings //20250208
        {
            public string ProxyHost;
            public int ProxyPort;
            public Settings(string proxyHost, int proxyPort)
            {
                ProxyHost = proxyHost;
                ProxyPort = proxyPort;
            }
        }
        #endregion

        /////////////////////////////////////////////////////
        ///2.参量
        #region        
        public Player Player { get; set; }// Flyleaf Player binded to FlyleafME (This can be swapped and will nto belong to this window)                
        public FlyleafME FlyleafME { get; set; }// FlyleafME Media Element Control
        public ICommand OpenWindow { get; set; }
        public ICommand CloseWindow { get; set; }

        static bool runOnce;
        static Settings settings = new Settings(PROXY_HOST, PROXY_PORT);

        Config playerConfig;
        bool ReversePlaybackChecked;

        string SampleVideo = Utils.FindFileBelow("Sample.mp4");
        const string URL_NTD = "http://cnhls.ntdtv.com";
        const string HOST_NTD = "ntdtv.com";
        const string PROXY_HOST = "127.0.0.1";
        const int PROXY_PORT = 8580;

        List<XtrUrl> XtrUrls = new(){
            new ("低频宽", "http://cnhls.ntdtv.com/cn/live150/playlist.m3u8" ),//first.m3u8?
            new ("中频宽", "http://cnhls.ntdtv.com/cn/live400/playlist.m3u8" ),
            new ("高频宽", "http://cnhls.ntdtv.com/cn/live800/playlist.m3u8" )
        };
        string DefaultCNUrlName = "中频宽";
        XtrUrl CurrentXtrUrl;
        string DefaultAspectRatioName = "Keep";
        bool IsCheckOpenAsyncRunning = false;

        private static string APP_PATH = Directory.GetCurrentDirectory();
        private string SETTINGS_JSON_FILE = APP_PATH + @"\settings.json";
        private static string FFMPEG_PATH = APP_PATH + @"\FFmpeg";
        private List<string> FFMPEG_FILES = new List<string>()
        {
            "avcodec-61.dll",
            "avdevice-61.dll",
            "avfilter-10.dll",
            "avformat-61.dll",
            "avutil-59.dll",
            "postproc-58.dll",
            "swresample-5.dll",
            "swscale-8.dll"
        };
        private string README_FILE = APP_PATH + @"\readme.md";
        #endregion

        /////////////////////////////////////////////////////
        ///3.初始化
        #region
        public MainWindow()
        {
            OpenWindow = new RelayCommandSimple(() => new MainWindow() { Width = Width, Height = Height }.Show());
            CloseWindow = new RelayCommandSimple(Close);

            FlyleafME = new FlyleafME(this)
            {
                Tag = this,
                ActivityTimeout = ACTIVITY_TIMEOUT,
                KeyBindings = AvailableWindows.Both,
                DetachedResize = AvailableWindows.Overlay,
                DetachedDragMove = AvailableWindows.Both,
                ToggleFullScreenOnDoubleClick
                                    = AvailableWindows.Both,
                KeepRatioOnResize = true,
                //20240315 以下两行禁用Drag和Drop媒体文件即播放的功能，作用点 在 FlyleafHost.cs 的
                //host.Surface.AllowDrop 和 host.Overlay.AllowDrop，因为此程序我单纯新唐人的播放器，播放无始无终
                //OpenOnDrop = AvailableWindows.None,//Both,
                //SwapOnDrop = AvailableWindows.None,

                PreferredLandscapeWidth = 800,
                PreferredPortraitHeight = 600,

                IsCustomizedForXtrPlayer = true,
            };

            // Allow Flyleaf WPF Control to Load UIConfig and Save both Config & UIConfig (Save button will be available in settings)
            FlyleafME.ConfigPath = "Flyleaf.Config.json";
            FlyleafME.EnginePath = "Flyleaf.Engine.json";
            FlyleafME.UIConfigPath = "Flyleaf.UIConfig.json";

            InitializeComponent();

            // Allowing FlyleafHost to access our Player
            DataContext = FlyleafME;

            //20240213
            CurrentXtrUrl = FindXTRUrl(DefaultCNUrlName);

            ParseMainJson();
        }

        private void Window_Initialized(object sender, EventArgs e)
        {
            //1.
            if (!CheckFFMpegFiles())
                return;
            //2.
            if (!File.Exists(Directory.GetCurrentDirectory() + "\\Flyleaf.UIConfig.json"))
            {
                Utils.Log("Flyleaf.UIConfig.json 不存在……");
                ShowAppInfoTips();
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Log("Window_Loaded.......");
            if (Engine.IsLoaded)
            {
                LoadPlayer();
                FlyleafME.Player = Player;
                //20240215
                CurrentXtrUrl = FindXTRUrl(DefaultCNUrlName);
                //Player.XtrQualityName = DefaultCNUrlName;
                //Play();
                Player.UpdateXtrQualityNameAndNotify(CurrentXtrUrl.QualityName);
                Player.UpdateAspectRatioNameAndNotify(DefaultAspectRatioName);
            }
            else
            {
                Engine.Loaded += (o, e) =>
                {
                    LoadPlayer();
                    Utils.UIInvokeIfRequired(() => FlyleafME.Player = Player);
                    //20240215
                    CurrentXtrUrl = FindXTRUrl(DefaultCNUrlName);
                    //Player.XtrQualityName = DefaultCNUrlName;
                    //Play();
                    Player.UpdateXtrQualityNameAndNotify(CurrentXtrUrl.QualityName);
                    Player.UpdateAspectRatioNameAndNotify(DefaultAspectRatioName);
                };
            }

            if (runOnce)
                return;
            runOnce = true;

            if (App.CmdUrl != null)
                Player.OpenAsync(App.CmdUrl);


#if RELEASE
            // Save Player's Config (First Run)
            // Ensures that the Control's handle has been created and the renderer has been fully initialized (so we can save also the filters parsed by the library)
            if (!playerConfig.Loaded)
            {
                try
                {
                    //Utils.AddFirewallRule();
                    playerConfig.Save("Flyleaf.Config.json");
                }
                catch { }
            }

            // Stops Logging (First Run)
            if (!Engine.Config.Loaded)
            {
                Engine.Config.LogOutput = null;
                Engine.Config.LogLevel = LogLevel.Quiet;
                //Engine.Config.FFmpegDevices  = false;

                try
                { Engine.Config.Save("Flyleaf.Engine.json"); }
                catch { }
            }
#endif
        }

        private void ParseMainJson()
        {
            try
            {
                if (!File.Exists(SETTINGS_JSON_FILE))
                {
                    SaveDefaultToMainJson();
                    return;
                }
                var jss = new JsonSerializerSettings();
                string jsonText = File.ReadAllText(SETTINGS_JSON_FILE);
                Settings s = JsonConvert.DeserializeObject<Settings>(jsonText, jss);
                Log("ParseMainJson: Parse Json OK...");
                if (!ValidateProxy(s)) return;
                settings.ProxyHost = s.ProxyHost;
                settings.ProxyPort = s.ProxyPort;
                Log("ParseMainJson: Check Proxy OK...");
            }
            catch (Exception e)
            {
                Log("ParseMainJson ERROR: " + e.Message);
            }
        }

        private bool ValidateProxy(Settings settings)
        {
            if (settings == null) return false;

            if (settings.ProxyHost == null || !Regex.IsMatch(settings.ProxyHost, @"^([\w-]+\.)+[\w-]+(/[\w-./?%&=]*)?$"))
            {
                MessageBoxError("代理主机配置错误！\n请修改settings.json，并从新运行本程序。\n若不修改，程序将使用默认值。");
                return false;
            }
            if (settings.ProxyPort < 1024 || settings.ProxyPort >= 65535)
            {
                MessageBoxError("代理端口配置错误，应在1024至65536之间！\n请修改settings.json，并从新运行本程序。\n若不修改，程序将使用默认值。");
                return false;
            }
            return true;
        }

        private void SaveDefaultToMainJson()
        {
            try
            {
                string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(SETTINGS_JSON_FILE, json);
            }
            catch (Exception e1)
            {
                Log(e1.Message);
            }
        }

        private bool CheckFFMpegFiles()
        {
            if (!Path.Exists(FFMPEG_PATH))
            {
                MessageBoxError("没有找到FFmpeg目录以及程序需要的dll文件！\n程序需要这些文件才能播放节目，请按照打开的说明文件下载！");
                System.Diagnostics.Process.Start("notepad.exe", README_FILE);
                Environment.Exit(0);
                return false;
            }
            List<string> files = new List<string>();
            DirectoryInfo root = new DirectoryInfo(FFMPEG_PATH);
            foreach (FileInfo f in root.GetFiles())
            {
                files.Add(f.Name);
            }
            var neededFiles = FFMPEG_FILES.Except(files).ToList<string>();
            if (neededFiles.Count > 0)
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < neededFiles.Count; i++)
                {
                    if (i < neededFiles.Count - 1)
                        sb.Append(neededFiles[i]).Append("、");
                    else
                        sb.Append(neededFiles[i]);
                }
                MessageBoxError($"没有在程序FFmpeg目录下找到所需要的{neededFiles.Count}个dll文件：{sb.ToString()}！\n程序需要这些文件才能播放节目，请按照打开的说明文件下载！");
                System.Diagnostics.Process.Start("notepad.exe", README_FILE);
                Environment.Exit(0);
                return false;
            }
            return true;
        }
        #endregion

        /////////////////////////////////////////////////////
        ///4.播放控制
        #region
        private void LoadPlayer()
        {
            // NOTE: Loads/Saves configs only in RELEASE mode

            // Player's Config (Cannot be initialized before Engine's initialization)
#if RELEASE
            // Load Player's Config
            if (File.Exists("Flyleaf.Config.json"))
                try
                { playerConfig = Config.Load("Flyleaf.Config.json"); }
                catch { playerConfig = DefaultConfig(); }
            else
                playerConfig = DefaultConfig();
#else
            playerConfig = DefaultConfig();
#endif

            //20240113 添加代理服务器
            if (playerConfig.Demuxer.FormatOpt.ContainsKey("http_proxy"))
            {
                playerConfig.Demuxer.FormatOpt.Remove("http_proxy");
            }
            playerConfig.Demuxer.FormatOpt.Add("http_proxy", $"http://{settings.ProxyHost}:{settings.ProxyPort}/");

#if DEBUG
            // Testing audio filters
            //playerConfig.Audio.Filters = new()
            //{
            ////new() { Name = "loudnorm", Args = "I=-24:LRA=7:TP=-2", Id = "loudnorm1" },
            ////new() { Name = "dynaudnorm", Args = "f=4150", Id = "dynaudnorm1" },
            ////new() { Name ="afftfilt", Args = "real='hypot(re,im)*sin(0)':imag='hypot(re,im)*cos(0)':win_size=512:overlap=0.75" }, // robot
            ////new() { Name ="tremolo", Args="f=5:d=0.5" },
            ////new() { Name ="vibrato", Args="f=10:d=0.5" },
            ////new() { Name ="rubberband", Args="pitch=1.5" }
            //};
#endif

            // Initializes the Player
            Player = new Player(playerConfig);

            // Dispose Player on Window Close (the possible swapped player from FlyleafMe that actually belongs to us)
            Closing += (o, e) => FlyleafME.Player?.Dispose();

            Player.BufferingStarted += (o, e) =>
            {
                Player.PlayInfo = "正在加载节目……";
                Player.IsShowProgress = true;
            };

            Player.BufferingCompleted += (o, e) =>
            {
                Player.PlayInfo = "加载节目完毕";
                Player.IsShowProgress = false;
            };

            //监控播放异常，异常，则暂停1秒，再次尝试
            Player.PlaybackStopped += (o, e) =>
            {
                Utils.Log("[Player.PlaybackStopped]: " + e.Success + ", " + e.Error);
                //特定条件下。因为 Ended 可能是网络不好，如果尝试没有意义。
                bool showErrorAndCheck = !string.IsNullOrEmpty(Player.Playlist.Url) && (Player.Status == Status.Failed || Player.Status == Status.Ended || (Player.Status == Status.Paused && !string.IsNullOrEmpty(e.Error)));
                /*Utils.UI(() =>
                {
                    PbWaiting.Visibility = showErrorAndCheck ? Visibility.Visible : Visibility.Collapsed;
                });*/
                Player.PlayInfo = showErrorAndCheck ? "播放出错，正在尝试从新播放，请耐心等待……" : null;
                Player.IsShowProgress = showErrorAndCheck;
                if (showErrorAndCheck)
                {
                    Utils.Log($"[Player.PlaybackStopped]: 从新播放：{Player.Playlist.Url}");

                    int delay = 1000;
                    if (Player.Status == Status.Failed || Player.Status == Status.Ended)
                    {
                        delay = 5000;
                    }
                    CheckAndPlayback(delay);
                }
            };
            //监控播放异常，异常，则暂停5秒，再次尝试
            Player.OpenCompleted += (o, e) =>
            {
                Utils.Log("[Player.OpenCompleted]: " + e.Success + ", " + e.Error);
                /*Utils.UI(() =>
                {
                    PbWaiting.Visibility = !e.Success ? Visibility.Visible : Visibility.Collapsed;
                });*/
                Player.PlayInfo = e.Success ? "正在加载节目……" : "播放出错，正在尝试从新播放，请耐心等待……";
                Player.IsShowProgress = true;// !e.Success;
                if (!e.Success)
                {
                    Utils.Log($"[Player.OpenCompleted]: 从新播放：{Player.Playlist.Url}");
                    //Player.PlayInfo = "播放出错，正在尝试从新播放，请耐心等待……";
                    CheckAndPlayback(5000);
                }
                else
                {
                    //成功打开，处于缓冲数据状态
                    CheckOpenState();
                }
            };


            // If the user requests reverse playback allocate more frames once
            Player.PropertyChanged += (o, e) =>
            {
                if (e.PropertyName == "ReversePlayback" && !GetWindowFromPlayer(Player).ReversePlaybackChecked)
                {
                    if (playerConfig.Decoder.MaxVideoFrames < 80)
                        playerConfig.Decoder.MaxVideoFrames = 80;

                    GetWindowFromPlayer(Player).ReversePlaybackChecked = true;
                }
                else if (e.PropertyName == nameof(Player.Rotation))
                    GetWindowFromPlayer(Player).Msg = $"旋转：{Player.Rotation}°";//$"Rotation {Player.Rotation}°";
                else if (e.PropertyName == nameof(Player.Speed))
                    GetWindowFromPlayer(Player).Msg = $"速度：x{Player.Speed}";
                else if (e.PropertyName == nameof(Player.Zoom))
                    GetWindowFromPlayer(Player).Msg = $"缩放：{Player.Zoom}%";
                else if (e.PropertyName == nameof(Player.Status) && Player.Activity.Mode == ActivityMode.Idle)
                    Player.Activity.ForceActive();
                else if (e.PropertyName == nameof(Player.IsShowDebug))
                {
                    FlyleafME.ShowDebug = Player.IsShowDebug;
                }
                else if (e.PropertyName == nameof(Player.XtrQualityName))
                {
                    CurrentXtrUrl = FindXTRUrl(Player.XtrQualityName);
                    FlyleafME.Player.UpdateXtrQualityName(CurrentXtrUrl.QualityName);
                    Play();
                }
                /*else if (e.PropertyName == nameof(Player.AspectRatioName))
                {
                    Utils.Log("[Changed]PropertyName: " + Player.AspectRatioName);
                    FlyleafME.Player.UpdateAspectRatioName(Player.AspectRatioName);
                }*/

            };

            Player.Audio.PropertyChanged += (o, e) =>
            {
                if (e.PropertyName == nameof(Player.Audio.Volume))
                    GetWindowFromPlayer(Player).Msg = $"音量：{Player.Audio.Volume}%"; //$"Volume {Player.Audio.Volume}%";
                else if (e.PropertyName == nameof(Player.Audio.Mute))
                    GetWindowFromPlayer(Player).Msg = Player.Audio.Mute ? "静音" : "";// "Muted" : "正常";
            };

            Player.Config.Audio.PropertyChanged += (o, e) =>
            {
                if (e.PropertyName == nameof(Player.Config.Audio.Delay))
                    GetWindowFromPlayer(Player).Msg = $"声音延迟：{Player.Config.Audio.Delay / 10000}ms";//Audio Delay
            };

            Player.Config.Subtitles.PropertyChanged += (o, e) =>
            {
                if (e.PropertyName == nameof(Player.Config.Subtitles.Delay))
                    GetWindowFromPlayer(Player).Msg = $"字幕延迟：{Player.Config.Subtitles.Delay / 10000}ms";//Subs Delay
            };

            // Ctrl+ N / Ctrl + W (Open New/Close Window)
            var key = playerConfig.Player.KeyBindings.Get("New Window");
            if (key != null)
                key.SetAction(() => (new MainWindow()).Show(), true);
            else
                playerConfig.Player.KeyBindings.AddCustom(Key.N, true, () => CreateNewWindow(Player), "New Window", false, true, false);

            key = playerConfig.Player.KeyBindings.Get("Close Window");
            if (key != null)
                key.SetAction(() => Close(), true);
            else
                playerConfig.Player.KeyBindings.AddCustom(Key.W, true, () => GetWindowFromPlayer(Player).Close(), "Close Window", false, true, false);
        }

        private void Play()
        {
            if (CurrentXtrUrl != null)
            {
                FlyleafME.Player.XtrQualityName = CurrentXtrUrl.QualityName;
                FlyleafME.Player.OpenAsync(CurrentXtrUrl.Url);
                CurrentXtrUrl = null;
                return;
            }
            //20240215 debug:
            //FlyleafME.Player.OpenAsync(SampleVideo);
        }

        private bool CheckInternetConnection_NoProxy()
        {
            try
            {
                Ping ping = new Ping();
                byte[] buffer = new byte[32];
                int timeout = 1000;
                PingOptions pingOptions = new PingOptions();
                PingReply reply = ping.Send(HOST_NTD, timeout, buffer, pingOptions);
                Utils.Log($"[CheckInternetConnection]: reply.Status = {reply.Status}");
                if (reply.Status == IPStatus.Success)
                {
                    return true;
                }
                else if (reply.Status == IPStatus.TimedOut)
                {
                    return false;//IsConnected;
                }
                else
                {
                    return false;
                }
            }
            catch (System.Exception e)
            {
                Utils.Log("[CheckInternetConnection]: " + e.Message);
                return false;
            }
        }

        private async Task<bool> CheckInternetConnection()
        {
            try
            {
                WebProxy myProxy = new WebProxy(settings.ProxyHost, settings.ProxyPort);
                var httpClientHandler = new HttpClientHandler { Proxy = myProxy };
                HttpClient client = new HttpClient(handler: httpClientHandler, disposeHandler: true);
                using HttpResponseMessage response = await client.GetAsync(URL_NTD);
                Utils.Log($"[CheckInternetConnection]: {response.StatusCode}");
                return response.StatusCode == HttpStatusCode.OK;
            }
            catch (System.Exception e)
            {
                Utils.Log("[CheckInternetConnection]: " + e.Message);
                return false;
            }
        }

        private async void CheckAndPlayback(int delay)
        {
            //1.
            while (true)
            {
                /*await Task.Run(async delegate
                {
                    await Task.Delay(delay);
                });*/
                await Task.Delay(delay);
                if (await CheckInternetConnection())
                {
                    Utils.Log("[CheckAndPlayback]: 网络连接正常");
                    break;
                }
                Utils.Log($"[CheckAndPlayback]: 网络连接不正常，继续等待 {delay} ms……");
            }
            Utils.Log("[CheckAndPlayback]: 网络连接正常，尝试从新播放……");
            Player.OpenAsync(Player.Playlist.Url);
            //2.

            CheckOpenState();
        }

        private void CheckOpenState()
        {
            if (IsCheckOpenAsyncRunning)
                return;
            Utils.Log("[CheckAndPlayback] ……");
            Task.Run(async () =>
            {
                IsCheckOpenAsyncRunning = true;
                while (true)
                {
                    Utils.Log("[CheckAndPlayback] 播放状态检测中……");
                    await Task.Delay(1000);
                    if (Player.CanPlay && Player.IsPlaying)
                    {

                        /*Utils.UI(() =>
                        {
                            PbWaiting.Visibility = Visibility.Collapsed;
                        });*/
                        Utils.Log("[CheckAndPlayback] 退出播放状态检测循环");
                        Player.PlayInfo = null;
                        Player.IsShowProgress = false;
                        IsCheckOpenAsyncRunning = false;
                        break;
                    }
                }
            });
        }

        private static MainWindow GetWindowFromPlayer(Player player)
        {
            FlyleafHost flhost = null;
            MainWindow mw = null;

            Utils.UIInvokeIfRequired(() =>
            {
                flhost = (FlyleafHost)player.Host;
                mw = (MainWindow)flhost.Overlay;
            });

            return mw;
        }
        private static void CreateNewWindow(Player player)
        {
            var mw = GetWindowFromPlayer(player);

            MainWindow mwNew = new()
            {
                Width = mw.Width,
                Height = mw.Height,
            };

            mwNew.Show();
        }
        #endregion

        /////////////////////////////////////////////////////
        ///5.工具函数
        #region
        private static string PatchProxyInfo()
        {
            if (settings == null) return "";
            return $"\n代理：{settings.ProxyHost}:{settings.ProxyPort}";
        }

        private XtrUrl FindXTRUrl(string urlName)
        {
            return XtrUrls.Find(item => item.QualityName == urlName);
        }

        private Config DefaultConfig()
        {
            Config config = new Config();
            config.Audio.FiltersEnabled = true;         // To allow embedded atempo filter for speed
            config.Video.GPUAdapter = "";           // Set it empty so it will include it when we save it
            config.Subtitles.SearchLocal = true;
            return config;
        }

        public void Log(string msg)
        {
            //Console.WriteLine(msg);
            Debug.WriteLine(msg);
        }

        private static string GetApplicationVersion()
        {
            System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
            System.Version version = assembly.GetName().Version;
            if (version == null)
                return "";
            return $" V{version.Major:D}.{version.Minor:D}.{version.Build:D4}.{version.Revision:D4}";
        }
        #endregion

        /////////////////////////////////////////////////////
        ///6. 控件控制与消息显示
        #region
        private void ShowAppInfoTips()
        {
            string info = "1、播放源\n" +
                "为动态网“动态网网站指南”中“新唐人电视”所提供的三个不同清晰度的中国频道播放源。\n" +
                "2、工作方式\n" +
                "借助自由门或无界，及其提供的代理来读取数据。所以，使用播放器时需要检查自由门或无界是否正常联网，以及使用环境所提供的代理是否正确。如果代理改变，请关闭程序并修改 settings.json ，从新运行程序即可。\n" +
                "3、播放中断的处理\n" +
                "播放器可以根据自由门或无界连接状态，自动应对播放中断的" +
                "状况以连续播放节目。因其依靠自由门或无界的连接，因此播放中断时间较长时，请及时检查自由门或无界的运行状态。";
            MessageBox.Show(this, info, "新唐人中国频道播放器简要说明", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void MessageBoxErrorWithoutResultOnUI(string message)
        {
            this.Dispatcher.Invoke(() =>
            {
                MessageBox.Show(this, message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }

        private bool MessageBoxInformation(string message)
        {
            return MessageBox.Show(this, message, "提示", MessageBoxButton.OK, MessageBoxImage.Information) == MessageBoxResult.OK;
        }

        private bool MessageBoxError(string message)
        {
            return MessageBox.Show(this, message, "错误", MessageBoxButton.OK, MessageBoxImage.Error) == MessageBoxResult.OK;
        }

        private bool MessageBoxQuestion(string message)
        {
            return MessageBox.Show(this, message, "询问", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
        }
        #endregion

        /////////////////////////////////////////////////////
        ///7. 一般控件事件
        #region
        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => FlyleafME.IsMinimized = true;
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
        private void BtnHelp_Click(object sender, RoutedEventArgs e)
        {
            ShowAppInfoTips();
        }
        #endregion

        /////////////////////////////////////////////////////
        ///8.OSD Msg
        #region OSD Msg
        CancellationTokenSource cancelMsgToken = new();
        public string Msg { get => msg; set { cancelMsgToken.Cancel(); msg = value; PropertyChanged?.Invoke(this, new(nameof(Msg))); cancelMsgToken = new(); Task.Run(FadeOutMsg, cancelMsgToken.Token); } }
        string msg;
        private async Task FadeOutMsg()
        {
            await Task.Delay(ACTIVITY_TIMEOUT, cancelMsgToken.Token);
            Utils.UIInvoke(() => { msg = ""; PropertyChanged?.Invoke(this, new(nameof(Msg))); });
        }
        #endregion




    }
}
