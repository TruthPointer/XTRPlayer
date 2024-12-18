﻿using System;
using System.IO;
using System.Threading;
using System.Windows;

using FlyleafLib;

namespace XTRPlayer
{
    public partial class App : Application
    {
        public static string CmdUrl { get; set; } = null;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (e.Args.Length == 1)
                CmdUrl = e.Args[0];

            // Ensures that we have enough worker threads to avoid the UI from freezing or not updating on time
            ThreadPool.GetMinThreads(out int workers, out int ports);
            ThreadPool.SetMinThreads(workers + 6, ports + 6);

            EngineConfig engineConfig;

            // Engine's Config
            #if RELEASE
            if (File.Exists("Flyleaf.Engine.json"))
                try { engineConfig = EngineConfig.Load("Flyleaf.Engine.json"); } catch { engineConfig = DefaultEngineConfig(); }
            else
                engineConfig = DefaultEngineConfig();
            #else
            engineConfig = DefaultEngineConfig();
            #endif

            Engine.StartAsync(engineConfig);
        }

        private EngineConfig DefaultEngineConfig()
        {
            EngineConfig engineConfig = new EngineConfig();

            engineConfig.PluginsPath    = ":Plugins";
            engineConfig.FFmpegPath     = ":FFmpeg";
            engineConfig.FFmpegHLSLiveSeek
                                        = true;
            engineConfig.UIRefresh      = true;
            engineConfig.FFmpegDevices  = true;

            #if RELEASE
            engineConfig.LogOutput      = "Flyleaf.FirstRun.log";
            engineConfig.LogLevel       = LogLevel.Debug;
            #else
            engineConfig.LogOutput      = ":debug";
            engineConfig.LogLevel       = LogLevel.Debug;
            engineConfig.FFmpegLogLevel = FFmpegLogLevel.Warning;
            #endif

            return engineConfig;
        }
    }
}
