using FlyleafLib.MediaFramework.MediaDecoder;
using System;
using System.Threading;
using System.Threading.Tasks;
using static FlyleafLib.Logger;
using static FlyleafLib.Utils;

namespace FlyleafLib.MediaPlayer;

partial class Player
{
    string stoppedWithError = null;
    //int tryTimesOnStatusPausedAndWithErrorInfo  = 0;
    //const int MaxTryTimesOnStatusPausedAndWithErrorInfo = 3;

    /// <summary>
    /// Fires on playback stopped by an error or completed / ended successfully <see cref="Status"/>
    /// Warning: Uses Invoke and it comes from playback thread so you can't pause/stop etc. You need to use another thread if you have to.
    /// </summary>
    public event EventHandler<PlaybackStoppedArgs> PlaybackStopped;
    protected virtual void OnPlaybackStopped(string error = null)
    {
        if (error != null && LastError == null)
        {
            lastError = error;
            UI(() => LastError = LastError);
        }

        PlaybackStopped?.Invoke(this, new PlaybackStoppedArgs(error));
    }

    /// <summary>
    /// Fires on seek completed for the specified ms (ms will be -1 on failure)
    /// </summary>
    public event EventHandler<int> SeekCompleted;

    /// <summary>
    /// Plays AVS streams
    /// </summary>
    public void Play()
    {
        lock (lockActions)
        {
            if (!CanPlay || Status == Status.Playing || Status == Status.Ended)
                return;

            status = Status.Playing;
            UI(() => Status = Status);
        }

        while (taskPlayRuns || taskSeekRuns)
            Thread.Sleep(5);
        taskPlayRuns = true;

        Thread t = new(() =>
        {
            try
            {
                Engine.TimeBeginPeriod1();
                NativeMethods.SetThreadExecutionState(NativeMethods.EXECUTION_STATE.ES_CONTINUOUS | NativeMethods.EXECUTION_STATE.ES_SYSTEM_REQUIRED | NativeMethods.EXECUTION_STATE.ES_DISPLAY_REQUIRED);

                onBufferingStarted   = 0;
                onBufferingCompleted = 0;
                requiresBuffering    = true;

                if (LastError != null)
                {
                    lastError = null;
                    UI(() => LastError = LastError);
                }

                if (Config.Player.Usage == Usage.Audio || !Video.IsOpened)
                    ScreamerAudioOnly();
                else
                {
                    if (ReversePlayback)
                    {
                        shouldFlushNext = true;
                        ScreamerReverse();
                    }
                    else
                    {
                        shouldFlushPrev = true;
                        Screamer();
                    }

                }

            }
            catch (Exception e)
            {
                Log.Error($"Playback failed ({e.Message})");
            }
            finally
            {
                VideoDecoder.DisposeFrame(vFrame);
                vFrame = null;

                if (Status == Status.Stopped)
                    decoder?.Initialize();
                else if (decoder != null)
                {
                    decoder.PauseOnQueueFull();
                    decoder.PauseDecoders();
                }

                Audio.ClearBuffer();
                Engine.TimeEndPeriod1();
                NativeMethods.SetThreadExecutionState(NativeMethods.EXECUTION_STATE.ES_CONTINUOUS);
                stoppedWithError = null;

                if (IsPlaying)
                {
                    if (decoderHasEnded)
                        status = Status.Ended;
                    else
                    {
                        if (Video.IsOpened && VideoDemuxer.Interrupter.Timedout)
                            stoppedWithError = "超时";// "Timeout";
                        else if (onBufferingStarted - 1 == onBufferingCompleted)
                        {
                            stoppedWithError = "播放意外停止，错误代码：0x01";//"Playback stopped unexpectedly";
                            OnBufferingCompleted("Buffering failed");
                        }
                        else
                        {
                            if (!ReversePlayback)
                            {
                                if (isLive || Math.Abs(Duration - CurTime) > 3 * 1000 * 10000)
                                    stoppedWithError = "播放意外停止，错误代码：0x02";//"Playback stopped unexpectedly";
                            }
                            else if (CurTime > 3 * 1000 * 10000)
                                stoppedWithError = "播放意外停止，错误代码：0x03";//"Playback stopped unexpectedly";
                        }

                        status = Status.Paused;
                    }
                }

                OnPlaybackStopped(stoppedWithError);
                if (CanDebug)
                    Log.Debug($"[SCREAMER] Finished (Status: {Status}, Error: {stoppedWithError})");

                UI(() =>
                {
                    Status = Status;
                    UpdateCurTime();
                });

                taskPlayRuns = false;
            }
        });
        t.Priority = Config.Player.ThreadPriority;
        t.Name = $"[#{PlayerId}] Playback";
        t.IsBackground = true;
        t.Start();
    }

    /// <summary>
    /// Pauses AVS streams
    /// </summary>
    public void Pause()
    {
        lock (lockActions)
        {
            if (!CanPlay || Status == Status.Ended)
                return;

            status = Status.Paused;
            UI(() => Status = Status);

            while (taskPlayRuns)
                Thread.Sleep(5);
        }
    }

    public void TogglePlayPause()
    {
        if (IsPlaying)
            Pause();
        else
        {
            //20240215 解决上次播放：播放出错或者播放完毕后，点击播放按钮再次播放的问题
            //20240313 对 Status = Status.Paused，且有出错信息的 自动尝试播放3次。
            Utils.Log($"TogglePlayPause---> 上次完成播放或当前播放出错的链接为：{Playlist.Url}");
            Utils.Log($"TogglePlayPause---> Status = {Status}，Message = {stoppedWithError}");
            if (!string.IsNullOrEmpty(Playlist.Url) && (Status == Status.Failed || Status == Status.Ended || (Status == Status.Paused && !string.IsNullOrEmpty(stoppedWithError))))
            {
                Utils.Log($"TogglePlayPause---> 从新播放：{Playlist.Url}");
                this.OpenAsync(Playlist.Url);
            }
            else
            {
                Play();
            }
        }
    }

    public void ToggleReversePlayback()
        => ReversePlayback = !ReversePlayback;

    /// <summary>
    /// Seeks backwards or forwards based on the specified ms to the nearest keyframe
    /// </summary>
    /// <param name="ms"></param>
    /// <param name="forward"></param>
    public void Seek(int ms, bool forward = false) => Seek(ms, forward, false);

    /// <summary>
    /// Seeks at the exact timestamp (with half frame distance accuracy)
    /// </summary>
    /// <param name="ms"></param>
    public void SeekAccurate(int ms)
        => Seek(ms, false, !IsLive);

    public void ToggleSeekAccurate()
        => Config.Player.SeekAccurate = !Config.Player.SeekAccurate;

    private void Seek(int ms, bool forward, bool accurate)
    {
        if (!CanPlay)
            return;

        lock (seeks)
        {
            _CurTime = curTime = ms * (long)10000;
            seeks.Push(new SeekData(ms, forward, accurate));
        }
        Raise(nameof(CurTime));

        if (Status == Status.Playing)
            return;

        lock (seeks)
        {
            if (taskSeekRuns)
                return;

            taskSeekRuns = true;
        }

        Task.Run(() =>
        {
            int ret;
            bool wasEnded = false;
            SeekData seekData = null;

            try
            {
                Engine.TimeBeginPeriod1();

                while (true)
                {
                    lock (seeks)
                    {
                        if (!(seeks.TryPop(out seekData) && CanPlay && !IsPlaying))
                        {
                            taskSeekRuns = false;
                            break;
                        }

                        seeks.Clear();
                    }

                    if (Status == Status.Ended)
                    {
                        wasEnded = true;
                        status = Status.Paused;
                        UI(() => Status = Status);
                    }

                    if (!Video.IsOpened)
                    {
                        if (AudioDecoder.OnVideoDemuxer)
                        {
                            ret = decoder.Seek(seekData.ms, seekData.forward);
                            if (CanWarn && ret < 0)
                                Log.Warn("Seek failed 2");

                            VideoDemuxer.Start();
                            SeekCompleted?.Invoke(this, -1);
                        }
                        else
                        {
                            ret = decoder.SeekAudio(seekData.ms, seekData.forward);
                            if (CanWarn && ret < 0)
                                Log.Warn("Seek failed 3");

                            AudioDemuxer.Start();
                            SeekCompleted?.Invoke(this, -1);
                        }

                        decoder.PauseOnQueueFull();
                        SeekCompleted?.Invoke(this, seekData.ms);
                    }
                    else
                    {
                        decoder.PauseDecoders();
                        ret = decoder.Seek(seekData.accurate ? Math.Max(0, seekData.ms - 3000) : seekData.ms, seekData.forward, !seekData.accurate); // 3sec ffmpeg bug for seek accurate when fails to seek backwards (see videodecoder getframe)
                        if (ret < 0)
                        {
                            if (CanWarn)
                                Log.Warn("Seek failed");
                            SeekCompleted?.Invoke(this, -1);
                        }
                        else if (!ReversePlayback && CanPlay)
                        {
                            decoder.GetVideoFrame(seekData.accurate ? seekData.ms * (long)10000 : -1);
                            ShowOneFrame();
                            VideoDemuxer.Start();
                            AudioDemuxer.Start();
                            SubtitlesDemuxer.Start();
                            decoder.PauseOnQueueFull();
                            SeekCompleted?.Invoke(this, seekData.ms);
                        }
                    }

                    Thread.Sleep(20);
                }
            }
            catch (Exception e)
            {
                lock (seeks)
                    taskSeekRuns = false;
                Log.Error($"Seek failed ({e.Message})");
            }
            finally
            {
                decoder.OpenedPlugin?.OnBufferingCompleted();
                Engine.TimeEndPeriod1();
                if ((wasEnded && Config.Player.AutoPlay) || stoppedWithError != null) // TBR: Possible race condition with if (Status == Status.Playing)
                    Play();
            }
        });
    }

    /// <summary>
    /// Flushes the buffer (demuxers (packets) and decoders (frames))
    /// This is useful mainly for live streams to push the playback at very end (low latency)
    /// </summary>
    public void Flush()
        => decoder.Flush();

    /// <summary>
    /// Stops and Closes AVS streams
    /// </summary>
    public void Stop()
    {
        lock (lockActions)
        {
            Initialize();
            renderer.Flush();
        }
    }
}

public class PlaybackStoppedArgs : EventArgs
{
    public string Error { get; }
    public bool Success { get; }

    public PlaybackStoppedArgs(string error)
    {
        Error = error;
        Success = Error == null;
    }
}

class SeekData
{
    public int  ms;
    public bool forward;
    public bool accurate;
    public SeekData(int ms, bool forward, bool accurate)
    { this.ms = ms; this.forward = forward && !accurate; this.accurate = accurate; }
}
