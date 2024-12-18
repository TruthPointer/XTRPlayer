# 本程序代碼裏 FFmpeg 目錄下的相关文件（用于播放视频）的説明

代碼和發佈的Release中的 FFmpeg 目錄是空的，請從下面鏈接下载：
 https://github.com/GyanD/codexffmpeg/releases/download/5.1.2/ffmpeg-5.1.2-full_build-shared.7z 
 下載后，先解壓，然後複製其 bin 目錄下除了 exe 文件之外的所有dll文件至程序代碼或所發佈的Release中的 FFMpeg 目录下。

> FFmpeg庫，可使用 Flyleaf 項目代碼附帶的  FFmpeg 目錄下的 dll 文件，也可以使用 Flyleaf 引用的 FFmpeg.AutoGen 所引用的  codexffmpeg 庫發佈的 release 版（https://github.com/GyanD/codexffmpeg/releases/download/5.1.2/ffmpeg-5.1.2-full_build-shared.7z ），其下 bin 目錄下的同名 dll 文件。也就是說，我們也可以自己審核和編譯 codexffmpeg。
補充説明下：Flyleaf 沒有直接使用 ffmpeg-5.1.2 編譯的 dll，是爲了解決 ***fixes #146***，此不影響程序的運行。