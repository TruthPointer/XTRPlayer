using FlyleafLib.Controls;
using FlyleafLib.MediaFramework.MediaContext;
using FlyleafLib.MediaFramework.MediaDecoder;
using FlyleafLib.MediaFramework.MediaDemuxer;
using FlyleafLib.MediaFramework.MediaFrame;
using FlyleafLib.MediaFramework.MediaPlaylist;
using FlyleafLib.MediaFramework.MediaRenderer;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using static FlyleafLib.Logger;
using static FlyleafLib.Utils;

namespace FlyleafLib.MediaPlayer;

public unsafe partial class Player : NotifyPropertyChanged, IDisposable
{
    #region Properties
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// FlyleafHost (WinForms, WPF or WinUI)
    /// </summary>
    public IHostPlayer Host { get => _Host; set => Set(ref _Host, value); }
    IHostPlayer _Host;

    /// <summary>
    /// Player's Activity (Idle/Active/FullActive)
    /// </summary>
    public Activity Activity { get; private set; }

    /// <summary>
    /// Helper ICommands for WPF MVVM
    /// </summary>
    public Commands Commands { get; private set; }

    public Playlist Playlist => decoder.Playlist;

    /// <summary>
    /// Player's Audio (In/Out)
    /// </summary>
    public Audio Audio { get; private set; }

    /// <summary>
    /// Player's Video
    /// </summary>
    public Video Video { get; private set; }

    /// <summary>
    /// Player's Subtitles
    /// </summary>
    public Subtitles Subtitles { get; private set; }

    /// <summary>
    /// Player's Renderer
    /// (Normally you should not access this directly)
    /// </summary>
    public Renderer renderer => decoder.VideoDecoder.Renderer;

    /// <summary>
    /// Player's Decoder Context
    /// (Normally you should not access this directly)
    /// </summary>
    public DecoderContext decoder { get; private set; }

    /// <summary>
    /// Audio Decoder
    /// (Normally you should not access this directly)
    /// </summary>
    public AudioDecoder AudioDecoder => decoder.AudioDecoder;

    /// <summary>
    /// Video Decoder
    /// (Normally you should not access this directly)
    /// </summary>
    public VideoDecoder VideoDecoder => decoder.VideoDecoder;

    /// <summary>
    /// Subtitles Decoder
    /// (Normally you should not access this directly)
    /// </summary>
    public SubtitlesDecoder SubtitlesDecoder => decoder.SubtitlesDecoder;

    /// <summary>
    /// Main Demuxer (if video disabled or audio only can be AudioDemuxer instead of VideoDemuxer)
    /// (Normally you should not access this directly)
    /// </summary>
    public Demuxer MainDemuxer => decoder.MainDemuxer;

    /// <summary>
    /// Audio Demuxer
    /// (Normally you should not access this directly)
    /// </summary>
    public Demuxer AudioDemuxer => decoder.AudioDemuxer;

    /// <summary>
    /// Video Demuxer
    /// (Normally you should not access this directly)
    /// </summary>
    public Demuxer VideoDemuxer => decoder.VideoDemuxer;

    /// <summary>
    /// Subtitles Demuxer
    /// (Normally you should not access this directly)
    /// </summary>
    public Demuxer SubtitlesDemuxer => decoder.SubtitlesDemuxer;


    /// <summary>
    /// Player's incremental unique id
    /// </summary>
    public int PlayerId { get; private set; }

    /// <summary>
    /// Player's configuration (set once in the constructor)
    /// </summary>
    public Config Config { get; protected set; }

    /// <summary>
    /// Player's Status
    /// </summary>
    public Status Status { get => status; private set => Set(ref _Status, value); }
    Status _Status = Status.Stopped, status = Status.Stopped;
    public bool IsPlaying => status == Status.Playing;

    /// <summary>
    /// Whether the player's status is capable of accepting playback commands
    /// </summary>
    public bool CanPlay { get => canPlay; internal set => Set(ref _CanPlay, value); }
    internal bool _CanPlay, canPlay;

    /// <summary>
    /// The list of chapters
    /// </summary>
    public List<Demuxer.Chapter>
                        Chapters => VideoDemuxer?.Chapters;

    /// <summary>
    /// Player's current time or user's current seek time (uses backward direction or accurate seek based on Config.Player.SeekAccurate)
    /// </summary>
    public long CurTime { get => curTime; set { if (Config.Player.SeekAccurate) SeekAccurate((int)(value / 10000)); else Seek((int)(value / 10000), false); } } // Note: forward seeking casues issues to some formats and can have serious delays (eg. dash with h264, dash with vp9 works fine)
    long _CurTime, curTime;
    internal void UpdateCurTime()
    {
        lock (seeks)
        {
            if (MainDemuxer == null || !seeks.IsEmpty)
                return;

            if (MainDemuxer.IsHLSLive)
            {
                curTime = MainDemuxer.CurTime; // *speed ?
                duration = MainDemuxer.Duration;
                Duration = Duration;
            }
        }

        Set(ref _CurTime, curTime, true, nameof(CurTime));

        UpdateBufferedDuration();
    }
    internal void UpdateBufferedDuration()
    {
        if (_BufferedDuration != MainDemuxer.BufferedDuration)
        {
            _BufferedDuration = MainDemuxer.BufferedDuration;
            Raise(nameof(BufferedDuration));
        }
    }

    /// <summary>
    /// Input's duration
    /// </summary>
    public long Duration { get => duration; private set => Set(ref _Duration, value); }
    long _Duration, duration;

    /// <summary>
    /// Forces Player's and Demuxer's Duration to allow Seek
    /// </summary>
    /// <param name="duration">Duration (Ticks)</param>
    /// <exception cref="ArgumentNullException">Demuxer must be opened before forcing the duration</exception>
    public void ForceDuration(long duration)
    {
        if (MainDemuxer == null)
            throw new ArgumentNullException(nameof(MainDemuxer));

        this.duration = duration;
        MainDemuxer.ForceDuration(duration);
        isLive = MainDemuxer.IsLive;
        UI(() =>
        {
            Duration = Duration;
            IsLive = IsLive;
        });
    }

    /// <summary>
    /// The current buffered duration in the demuxer
    /// </summary>
    public long BufferedDuration
    {
        get => MainDemuxer == null ? 0 : MainDemuxer.BufferedDuration;
        internal set => Set(ref _BufferedDuration, value);
    }
    long _BufferedDuration;

    /// <summary>
    /// Whether the input is live (duration might not be 0 on live sessions to allow live seek, eg. hls)
    /// </summary>
    public bool IsLive { get => isLive; private set => Set(ref _IsLive, value); }
    bool _IsLive, isLive;

    ///// <summary>
    ///// Total bitrate (Kbps)
    ///// </summary>
    public double BitRate { get => bitRate; internal set => Set(ref _BitRate, value); }
    internal double _BitRate, bitRate;

    /// <summary>
    /// Whether the player is recording
    /// </summary>
    public bool IsRecording
    {
        get => decoder != null && decoder.IsRecording;
        private set { if (_IsRecording == value) return; _IsRecording = value; UI(() => Set(ref _IsRecording, value, false)); }
    }
    bool _IsRecording;

    /// <summary>
    /// Pan X Offset to change the X location
    /// </summary>
    public int PanXOffset { get => renderer.PanXOffset; set { renderer.PanXOffset = value; Raise(nameof(PanXOffset)); } }

    /// <summary>
    /// Pan Y Offset to change the Y location
    /// </summary>
    public int PanYOffset { get => renderer.PanYOffset; set { renderer.PanYOffset = value; Raise(nameof(PanYOffset)); } }

    /// <summary>
    /// Playback's speed (x1 - x4)
    /// </summary>
    public double Speed
    {
        get => speed;
        set
        {
            double newValue = Math.Round(value, 3);
            if (value < 0.125)
                newValue = 0.125;
            else if (value > 16)
                newValue = 16;

            if (newValue == speed || (newValue > 1 && ReversePlayback))
                return;

            AudioDecoder.Speed = newValue;
            VideoDecoder.Speed = newValue;
            speed = newValue;
            decoder.RequiresResync = true;
            requiresBuffering = true;
            Subtitles.subsText = "";

            UI(() =>
            {
                Subtitles.SubsText = Subtitles.SubsText;
                Raise(nameof(Speed));
            });
        }
    }
    double speed = 1;

    /// <summary>
    /// Pan zoom percentage (100 for 100%)
    /// </summary>
    public int Zoom
    {
        get => (int)(renderer.Zoom * 100);
        set { renderer.SetZoom(renderer.Zoom = value / 100.0); RaiseUI(nameof(Zoom)); }
        //set { renderer.SetZoomAndCenter(renderer.Zoom = value / 100.0, Renderer.ZoomCenterPoint); RaiseUI(nameof(Zoom)); } // should reset the zoom center point?
    }

    /// <summary>
    /// Pan rotation angle (for D3D11 VP allowed values are 0, 90, 180, 270 only)
    /// </summary>
    public uint Rotation
    {
        get => renderer.Rotation;
        set
        {
            renderer.Rotation = value;
            RaiseUI(nameof(Rotation));
        }
    }

    /// <summary>
    /// Pan Horizontal Flip (FlyleafVP only)
    /// </summary>
    public bool HFlip { get => renderer.HFlip; set => renderer.HFlip = value; }

    /// <summary>
    /// Pan Vertical Flip (FlyleafVP only)
    /// </summary>
    public bool VFlip { get => renderer.VFlip; set => renderer.VFlip = value; }

    /// <summary>
    /// Whether to use reverse playback mode
    /// </summary>
    public bool ReversePlayback
    {
        get => _ReversePlayback;

        set
        {
            if (_ReversePlayback == value)
                return;

            _ReversePlayback = value;
            UI(() => Set(ref _ReversePlayback, value, false));

            if (!Video.IsOpened || !CanPlay | IsLive)
                return;

            lock (lockActions)
            {
                bool shouldPlay = IsPlaying || (Status == Status.Ended && Config.Player.AutoPlay);
                Pause();
                sFrame = null;
                Subtitles.subsText = "";
                if (Subtitles._SubsText != "")
                    UI(() => Subtitles.SubsText = Subtitles.SubsText);
                decoder.StopThreads();
                decoder.Flush();

                if (Status == Status.Ended)
                {
                    status = Status.Paused;
                    UI(() => Status = Status);
                }

                if (value)
                {
                    Speed = 1;
                    VideoDemuxer.EnableReversePlayback(CurTime);
                }
                else
                {
                    VideoDemuxer.DisableReversePlayback();

                    var vFrame = VideoDecoder.GetFrame(VideoDecoder.GetFrameNumber(CurTime));
                    VideoDecoder.DisposeFrame(vFrame);
                    vFrame = null;
                    decoder.RequiresResync = true;
                }

                reversePlaybackResync = false;
                if (shouldPlay)
                    Play();
            }
        }
    }
    bool _ReversePlayback;

    public object Tag { get => tag; set => Set(ref tag, value); }
    object tag;

    public string LastError { get => lastError; set => Set(ref _LastError, value); }
    string _LastError, lastError;
    bool decoderHasEnded => decoder != null && (VideoDecoder.Status == MediaFramework.Status.Ended || (VideoDecoder.Disposed && AudioDecoder.Status == MediaFramework.Status.Ended));

    public bool IsShowProgress { get => _IsShowProgress; set => Set(ref _IsShowProgress, value); }//20240317
    bool _IsShowProgress = false;

    public string PlayInfo { get => _PlayInfo; set => Set(ref _PlayInfo, value); }//20240317
    string _PlayInfo;

    public bool IsShowDebug { get => _IsShowDebug; private set => Set(ref _IsShowDebug, value); }
    bool _IsShowDebug;
    public void UpdateIsShowDebugAndNotify(bool isChecked)
    {
        if (_IsShowDebug != isChecked)
        {
            _IsShowDebug = isChecked;
            Raise(nameof(IsShowDebug));
        }
    }

    //---------------------------------------------------
    //20240115
    public string XtrQualityName { get; set; } = "";

    public void UpdateXtrQualityName(string name)
    {
        if (XtrQualityName != name)
        {
            XtrQualityName = name;
        }
    }
    public void UpdateXtrQualityNameAndNotify(string name)
    {
        Utils.Log("UpdateXtrQualityNameAndNotify -->" + name);
        if (XtrQualityName != name)
        {
            XtrQualityName = name;
            Raise(nameof(XtrQualityName));

            if (name == "低频宽")
            {
                UpdateXtrLowQualityAndNotify(true);
                UpdateXtrMedQualityAndNotify(false);
                UpdateXtrHighQualityAndNotify(false);
            }
            else if (name == "中频宽")
            {
                UpdateXtrLowQualityAndNotify(false);
                UpdateXtrMedQualityAndNotify(true);
                UpdateXtrHighQualityAndNotify(false);
            }
            else //(tName == "高频宽")
            {
                UpdateXtrLowQualityAndNotify(false);
                UpdateXtrMedQualityAndNotify(false);
                UpdateXtrHighQualityAndNotify(true);
            }
        }
    }

    public bool IsXtrHighQuality { get => _IsXtrHighQuality; private set => Set(ref _IsXtrHighQuality, value); }
    bool _IsXtrHighQuality;
    public void UpdateXtrHighQualityAndNotify(bool isChecked)
    {
        if (_IsXtrHighQuality != isChecked)
        {
            _IsXtrHighQuality = isChecked;
            Raise(nameof(IsXtrHighQuality));
        }
    }

    public bool IsXtrMedQuality { get => _IsXtrMedQuality; private set => Set(ref _IsXtrMedQuality, value); }
    bool _IsXtrMedQuality;
    public void UpdateXtrMedQualityAndNotify(bool isChecked)
    {
        if (_IsXtrMedQuality != isChecked)
        {
            _IsXtrMedQuality = isChecked;
            Raise(nameof(IsXtrMedQuality));
        }
    }
    public bool IsXtrLowQuality { get => _IsXtrLowQuality; private set => Set(ref _IsXtrLowQuality, value); }
    bool _IsXtrLowQuality;
    public void UpdateXtrLowQualityAndNotify(bool isChecked)
    {
        if (_IsXtrLowQuality != isChecked)
        {
            _IsXtrLowQuality = isChecked;
            Raise(nameof(IsXtrLowQuality));
        }
    }

    //-------------------------------------
    //20240324
    public string AspectRatioName { get; set; } = "";

    public void UpdateAspectRatioName(string name)
    {
        if (AspectRatioName != name)
        {
            UpdateAspectRatioNameAndNotify(name);//20240225
            AspectRatioName = name;
        }
    }

    public void UpdateAspectRatioNameAndNotify(string name)
    {
        Utils.Log($"UpdateAspectRatioNameAndNotify --> current = {AspectRatioName}, new = {name}");
        if (AspectRatioName != name)
        {
            AspectRatioName = name;
            Raise(nameof(AspectRatioName));

            if (name == "Keep")
            {
                UpdateAspectRationKeep(true);
                UpdateAspectRationFill(false);
                UpdateAspectRation1To1(false);
                UpdateAspectRation4To3(false);
                UpdateAspectRation16To9(false);
                UpdateAspectRation16To10(false);
                UpdateAspectRation2p35To1(false);
            }
            else if (name == "Fill")
            {
                UpdateAspectRationKeep(false);
                UpdateAspectRationFill(true);
                UpdateAspectRation1To1(false);
                UpdateAspectRation4To3(false);
                UpdateAspectRation16To9(false);
                UpdateAspectRation16To10(false);
                UpdateAspectRation2p35To1(false);
            }
            else if (name == "1:1")
            {
                UpdateAspectRationKeep(false);
                UpdateAspectRationFill(false);
                UpdateAspectRation1To1(true);
                UpdateAspectRation4To3(false);
                UpdateAspectRation16To9(false);
                UpdateAspectRation16To10(false);
                UpdateAspectRation2p35To1(false);
            }
            else if (name == "4:3")
            {
                UpdateAspectRationKeep(false);
                UpdateAspectRationFill(false);
                UpdateAspectRation1To1(false);
                UpdateAspectRation4To3(true);
                UpdateAspectRation16To9(false);
                UpdateAspectRation16To10(false);
                UpdateAspectRation2p35To1(false);
            }
            else if (name == "16:9")
            {
                UpdateAspectRationKeep(false);
                UpdateAspectRationFill(false);
                UpdateAspectRation1To1(false);
                UpdateAspectRation4To3(false);
                UpdateAspectRation16To9(true);
                UpdateAspectRation16To10(false);
                UpdateAspectRation2p35To1(false);
            }
            else if (name == "16:10")
            {
                UpdateAspectRationKeep(false);
                UpdateAspectRationFill(false);
                UpdateAspectRation1To1(false);
                UpdateAspectRation4To3(false);
                UpdateAspectRation16To9(false);
                UpdateAspectRation16To10(true);
                UpdateAspectRation2p35To1(false);
            }
            else if (name == "2.35:1")
            {
                UpdateAspectRationKeep(false);
                UpdateAspectRationFill(false);
                UpdateAspectRation1To1(false);
                UpdateAspectRation4To3(false);
                UpdateAspectRation16To9(false);
                UpdateAspectRation16To10(false);
                UpdateAspectRation2p35To1(true);
            }
        }
    }

    public bool IsAspectRationKeep { get => _IsAspectRationKeep; private set => Set(ref _IsAspectRationKeep, value); }
    bool _IsAspectRationKeep;
    public void UpdateAspectRationKeep(bool isChecked)
    {
        if (_IsAspectRationKeep != isChecked)
        {
            _IsAspectRationKeep = isChecked;
            Raise(nameof(IsAspectRationKeep));
        }
    }

    public bool IsAspectRationFill { get => _IsAspectRationFill; private set => Set(ref _IsAspectRationFill, value); }
    bool _IsAspectRationFill;
    public void UpdateAspectRationFill(bool isChecked)
    {
        if (_IsAspectRationFill != isChecked)
        {
            _IsAspectRationFill = isChecked;
            Raise(nameof(IsAspectRationFill));
        }
    }

    public bool IsAspectRation1To1 { get => _IsAspectRation1To1; private set => Set(ref _IsAspectRation1To1, value); }
    bool _IsAspectRation1To1;
    public void UpdateAspectRation1To1(bool isChecked)
    {
        if (_IsAspectRation1To1 != isChecked)
        {
            _IsAspectRation1To1 = isChecked;
            Raise(nameof(IsAspectRation1To1));
        }
    }

    public bool IsAspectRation4To3 { get => _IsAspectRation4To3; private set => Set(ref _IsAspectRation4To3, value); }
    bool _IsAspectRation4To3;
    public void UpdateAspectRation4To3(bool isChecked)
    {
        if (_IsAspectRation4To3 != isChecked)
        {
            _IsAspectRation4To3 = isChecked;
            Raise(nameof(IsAspectRation4To3));
        }
    }

    public bool IsAspectRation16To9 { get => _IsAspectRation16To9; private set => Set(ref _IsAspectRation16To9, value); }
    bool _IsAspectRation16To9;
    public void UpdateAspectRation16To9(bool isChecked)
    {
        if (_IsAspectRation16To9 != isChecked)
        {
            _IsAspectRation16To9 = isChecked;
            Raise(nameof(IsAspectRation16To9));
        }
    }

    public bool IsAspectRation16To10 { get => _IsAspectRation16To10; private set => Set(ref _IsAspectRation16To10, value); }
    bool _IsAspectRation16To10;
    public void UpdateAspectRation16To10(bool isChecked)
    {
        if (_IsAspectRation16To10 != isChecked)
        {
            _IsAspectRation16To10 = isChecked;
            Raise(nameof(IsAspectRation16To10));
        }
    }

    public bool IsAspectRation2p35To1 { get => _IsAspectRation2p35To1; private set => Set(ref _IsAspectRation2p35To1, value); }
    bool _IsAspectRation2p35To1;
    public void UpdateAspectRation2p35To1(bool isChecked)
    {
        if (_IsAspectRation2p35To1 != isChecked)
        {
            _IsAspectRation2p35To1 = isChecked;
            Raise(nameof(IsAspectRation2p35To1));
        }
    }
    #endregion

    #region Properties Internal
    readonly object lockActions  = new();
    readonly object lockSubtitles= new();

    bool taskSeekRuns;
    bool taskPlayRuns;
    bool taskOpenAsyncRuns;

    readonly ConcurrentStack<SeekData>   seeks      = new();
    readonly ConcurrentQueue<Action>     UIActions  = new();

    internal AudioFrame     aFrame;
    internal VideoFrame     vFrame;
    internal SubtitlesFrame sFrame, sFramePrev;
    internal PlayerStats    stats = new();
    internal LogHandler     Log;

    internal bool requiresBuffering;
    bool reversePlaybackResync;

    bool isVideoSwitch;
    bool isAudioSwitch;
    bool isSubsSwitch;
    #endregion

    public Player(Config config = null)
    {
        if (config != null)
        {
            if (config.Player.player != null)
                throw new Exception("Player's configuration is already assigned to another player");

            Config = config;
        }
        else
            Config = new Config();

        PlayerId = GetUniqueId();
        Log = new LogHandler(("[#" + PlayerId + "]").PadRight(8, ' ') + " [Player        ] ");
        Log.Debug($"Creating Player (Usage = {Config.Player.Usage})");

        Activity = new Activity(this);
        Audio = new Audio(this);
        Video = new Video(this);
        Subtitles = new Subtitles(this);
        Commands = new Commands(this);

        Config.SetPlayer(this);

        if (Config.Player.Usage == Usage.Audio)
        {
            Config.Video.Enabled = false;
            Config.Subtitles.Enabled = false;
        }

        decoder = new DecoderContext(Config, PlayerId) { Tag = this };
        Engine.AddPlayer(this);

        if (decoder.VideoDecoder.Renderer != null)
            decoder.VideoDecoder.Renderer.forceNotExtractor = true;

        //decoder.OpenPlaylistItemCompleted              += Decoder_OnOpenExternalSubtitlesStreamCompleted;

        decoder.OpenAudioStreamCompleted += Decoder_OpenAudioStreamCompleted;
        decoder.OpenVideoStreamCompleted += Decoder_OpenVideoStreamCompleted;
        decoder.OpenSubtitlesStreamCompleted += Decoder_OpenSubtitlesStreamCompleted;

        decoder.OpenExternalAudioStreamCompleted += Decoder_OpenExternalAudioStreamCompleted;
        decoder.OpenExternalVideoStreamCompleted += Decoder_OpenExternalVideoStreamCompleted;
        decoder.OpenExternalSubtitlesStreamCompleted += Decoder_OpenExternalSubtitlesStreamCompleted;

        AudioDecoder.CBufAlloc = () => { if (aFrame != null) aFrame.dataPtr = IntPtr.Zero; aFrame = null; Audio.ClearBuffer(); aFrame = null; };
        AudioDecoder.CodecChanged = Decoder_AudioCodecChanged;
        VideoDecoder.CodecChanged = Decoder_VideoCodecChanged;
        decoder.RecordingCompleted += (o, e) => { IsRecording = false; };

        status = Status.Stopped;
        Reset();
        Log.Debug("Created");
    }

    /// <summary>
    /// Disposes the Player and de-assigns it from FlyleafHost
    /// </summary>
    public void Dispose() => Engine.DisposePlayer(this);
    internal void DisposeInternal()
    {
        lock (lockActions)
        {
            if (IsDisposed)
                return;

            try
            {
                Initialize();
                Audio.Dispose();
                decoder.Dispose();
                Host?.Player_Disposed();
                Log.Info("Disposed");
            }
            catch (Exception e) { Log.Warn($"Disposed ({e.Message})"); }

            IsDisposed = true;
        }
    }
    internal void RefreshMaxVideoFrames()
    {
        lock (lockActions)
        {
            if (!Video.isOpened)
                return;

            bool wasPlaying = IsPlaying;
            Pause();
            VideoDecoder.RefreshMaxVideoFrames();
            ReSync(decoder.VideoStream, (int)(CurTime / 10000), true);

            if (wasPlaying)
                Play();
        }
    }

    private void ResetMe()
    {
        canPlay = false;
        bitRate = 0;
        curTime = 0;
        duration = 0;
        isLive = false;
        lastError = null;

        UIAdd(() =>
        {
            BitRate = BitRate;
            Duration = Duration;
            IsLive = IsLive;
            Status = Status;
            CanPlay = CanPlay;
            LastError = LastError;
            BufferedDuration = 0;
            Set(ref _CurTime, curTime, true, nameof(CurTime));
        });
    }
    private void Reset()
    {
        ResetMe();
        Video.Reset();
        Audio.Reset();
        Subtitles.Reset();
        UIAll();
    }
    private void Initialize(Status status = Status.Stopped, bool andDecoder = true, bool isSwitch = false)
    {
        if (CanDebug)
            Log.Debug($"Initializing");

        lock (lockActions) // Required in case of OpenAsync and Stop requests
        {
            try
            {
                Engine.TimeBeginPeriod1();

                this.status = status;
                canPlay = false;
                isVideoSwitch = false;
                seeks.Clear();

                while (taskPlayRuns || taskSeekRuns)
                    Thread.Sleep(5);

                if (andDecoder)
                {
                    if (isSwitch)
                        decoder.InitializeSwitch();
                    else
                        decoder.Initialize();
                }

                Reset();
                VideoDemuxer.DisableReversePlayback();
                ReversePlayback = false;

                if (CanDebug)
                    Log.Debug($"Initialized");

            }
            catch (Exception e)
            {
                Log.Error($"Initialize() Error: {e.Message}");

            }
            finally
            {
                Engine.TimeEndPeriod1();
            }
        }
    }

    internal void UIAdd(Action action) => UIActions.Enqueue(action);
    internal void UIAll()
    {
        while (!UIActions.IsEmpty)
            if (UIActions.TryDequeue(out var action))
                UI(action);
    }

    public override bool Equals(object obj)
        => obj == null || !(obj is Player) ? false : ((Player)obj).PlayerId == PlayerId;
    public override int GetHashCode() => PlayerId.GetHashCode();

    // Avoid having this code in OnPaintBackground as it can cause designer issues (renderer will try to load FFmpeg.Autogen assembly because of HDR Data)
    internal bool WFPresent() { if (renderer == null || renderer.SCDisposed) return false; renderer?.Present(); return true; }
}

public enum Status
{
    Opening,
    Failed,
    Stopped,
    Paused,
    Playing,
    Ended
}
public enum Usage
{
    AVS,
    Audio
}