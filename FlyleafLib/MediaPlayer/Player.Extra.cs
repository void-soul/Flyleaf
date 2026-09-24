using FlyleafLib.MediaFramework.MediaDecoder;
using FlyleafLib.MediaFramework.MediaDemuxer;
using System.Drawing.Imaging;
using System.Windows;

namespace FlyleafLib.MediaPlayer;

unsafe partial class Player
{
    public bool IsOpenFileDialogOpen { get; private set; }


    public void SeekBackward() => SeekBackward_(Config.Player.SeekOffset);
    public void SeekBackward2() => SeekBackward_(Config.Player.SeekOffset2);
    public void SeekBackward3() => SeekBackward_(Config.Player.SeekOffset3);
    public void SeekBackward_(long offset)
    {
        if (!CanPlay)
            return;

        long seekTs = curTime - (curTime % offset) - offset;

        if (Config.Player.SeekAccurate)
            SeekAccurate(Math.Max((int)(seekTs / 10000), 0));
        else
            Seek(Math.Max((int)(seekTs / 10000), 0), false);
    }

    public void SeekForward() => SeekForward_(Config.Player.SeekOffset);
    public void SeekForward2() => SeekForward_(Config.Player.SeekOffset2);
    public void SeekForward3() => SeekForward_(Config.Player.SeekOffset3);
    public void SeekForward_(long offset)
    {
        if (!CanPlay)
            return;

        long seekTs = curTime - (curTime % offset) + offset;

        if (seekTs > duration && !isLive)
            return;

        if (Config.Player.SeekAccurate)
            SeekAccurate((int)(seekTs / 10000));
        else
            Seek((int)(seekTs / 10000), true);
    }

    public void SeekToStart() => Seek(0);
    public void SeekToEnd() => Seek((int)((Duration / 10_000) - TimeSpan.FromSeconds(5).TotalMilliseconds));

    public void SeekToChapter(Demuxer.Chapter chapter) =>
        /* TODO
* Accurate pts required (backward/forward check)
* Get current chapter implementation + next/prev
*/
        Seek((int)(chapter.StartTime / 10000.0), true);

    public void CopyToClipboard()
    {
        if (Playlist.Url == null)
            Clipboard.SetText("");
        else
            Clipboard.SetText(Playlist.Url);
    }
    public void CopyItemToClipboard()
    {
        if (Playlist.Selected == null || Playlist.Selected.DirectUrl == null)
            Clipboard.SetText("");
        else
            Clipboard.SetText(Playlist.Selected.DirectUrl);
    }
    public void OpenFromClipboard()
        => OpenAsync(Clipboard.GetText());
    public void OpenFromFileDialog()
    {
        bool wasActivityEnabled = Activity.IsEnabled;
        Activity.IsEnabled = false;
        IsOpenFileDialogOpen = true;

        System.Windows.Forms.OpenFileDialog openFileDialog = new();
        var res = openFileDialog.ShowDialog();

        if (res == System.Windows.Forms.DialogResult.OK)
            OpenAsync(openFileDialog.FileName);

        Activity.IsEnabled = wasActivityEnabled;
        IsOpenFileDialogOpen = false;
    }

    public void ShowFrame(int frameIndex)
    {
        if (!Video.IsOpened || !CanPlay || VideoDemuxer.IsHLSLive) return;

        lock (lockActions)
        {
            Pause();
            dFrame = null;
            sFrame = null;
            Renderer.SubsDispose();
            Subtitles.ClearSubsText();
            decoder.Flush();
            decoder.RequiresResync = true;

            var vFrame = VideoDecoder.GetFrame(frameIndex);
            if (vFrame == null)
                return;

            if (CanDebug) Log.Debug($"SFI: {VideoDecoder.GetFrameNumber(vFrame.Timestamp)}");
            vFrames.Enqueue(vFrame, true);
            Renderer.RenderRequest(vFrame);
            UpdateCurTime(vFrame.Timestamp);
            reversePlaybackResync = true;
        }
    }

    // Whether video queue should be flushed as it could have opposite direction frames
    bool shouldFlushNext;
    bool shouldFlushPrev;
    public void ShowFrameNext()
    {
        if (!Video.IsOpened || !canPlay || VideoDemuxer.IsHLSLive)
            return;

        lock (lockActions)
        {
            Pause();

            if (status == Status.Ended)
            {
                status = Status.Paused;
                UI(() => Status = status);
            }

            shouldFlushPrev = true;
            decoder.RequiresResync = true;

            if (shouldFlushNext)
            {
                decoder.StopThreads();
                decoder.Flush();
                shouldFlushNext = false;

                VideoDecoder.GetFrame(VideoDecoder.GetFrameNumber(curTime))?.Dispose();
            }

            sFrame = null;
            Subtitles.ClearSubsText();
            Renderer.SubsDispose();

            if (!vFrames.TryDequeue(out var vFrame))
            {
                Renderer.Frames.PushCurrentToLast();
                vFrame = VideoDecoder.GetFrameNext();
                if (vFrame == null) return;
                vFrames.Enqueue(vFrame, true);
            }

            if (CanDebug) Log.Debug($"SFN: {VideoDecoder.GetFrameNumber(vFrame.Timestamp)}");

            Renderer.RenderRequest(vFrame);
            UpdateCurTime(vFrame.Timestamp);
            reversePlaybackResync = true;
        }
    }
    /// <summary> Q-0431：最近一次逐帧后退的滑动窗口缓存查询细节（Flyleaf 的 Log 不落盘，由宿主打印到 [StepCache] 行）。 </summary>
    public string? LastStepCacheDiag { get; private set; }

    /// <param name="step">Q-0427：一次后退的帧数（默认 1 = 逐帧；5F/10F 帧档传 5/10，
    /// 走同一条缓存路径——命中滑动窗口即免 seek）。</param>
    public void ShowFramePrev(int step = 1)
    {
        if (!Video.IsOpened || !canPlay || VideoDemuxer.IsHLSLive)
            return;

        lock (lockActions)
        {
            Pause();

            if (status == Status.Ended)
            {
                status = Status.Paused;
                UI(() => Status = status);
            }

            shouldFlushNext = true;
            decoder.RequiresResync = true;

            if (shouldFlushPrev)
            {
                decoder.StopThreads();
                decoder.Flush();
                shouldFlushPrev = false;
            }

            sFrame = null;
            Subtitles.ClearSubsText();
            Renderer.SubsDispose();

            if (!vFrames.TryDequeue(out var vFrame))
            {
                reversePlaybackResync = true; // Temp fix for previous timestamps until we seperate GetFrame for Extractor and the Player
                Renderer.Frames.PushCurrentToLast();

                // Q-0427：逐帧后退先查滑动窗口缓存——命中即取（无 seek、无解码，~0ms）。
                // step>1（5F/10F 帧档）走同一条路径：一次跳 step 帧，缓存命中同样免费。
                int targetFrame = Math.Max(0, VideoDecoder.GetFrameNumber(CurTime) - step);
                // Q-0431：按时间轴（而非帧号）查缓存，避免 demuxer.StartTime 造成的口径错位
                if (!VideoDecoder.TryGetCachedStepFrame(CurTime, step, out vFrame))
                    // 未命中：走原 seek 路径，并把解码路径上的帧一并填入滑动窗口缓存
                    // （原本这些帧会被全部丢弃，导致每退一帧都要重新 seek —— GOP=15 时 310ms/步）。
                    // Q-0464：把 step 传进去，填充时按帧档抽稀 —— 窗口跨度 K×step 帧、
                    // 只存"距目标 step 整数倍"的帧，让 K 个名额在 5F/10F 下也能覆盖 K 步。
                    vFrame = VideoDecoder.GetFrame(targetFrame, true, VideoDecoder.StepCache, step);

                if (vFrame == null) return;
                vFrames.Enqueue(vFrame, true);
                // Q-0431：把缓存查询细节暴露给宿主打印（Flyleaf 自己的 Log 不落盘）
                LastStepCacheDiag = VideoDecoder.StepCacheDiag;
            }
            else
            {
                // Q-0432b：本步走队列快速路径（未经缓存查询）—— 清掉上一步的诊断串，避免宿主重复打旧值
                LastStepCacheDiag = null;
            }

            if (CanDebug) Log.Debug($"SFB: {VideoDecoder.GetFrameNumber(vFrame.Timestamp)}");

            Renderer.RenderRequest(vFrame);
            UpdateCurTime(vFrame.Timestamp);
        }
    }

    /// <summary> Q-0488：滑动窗口缓存里当前剩余的帧数（0 = 下一次后退必然 seek + 解码）。 </summary>
    public int StepCacheCount => VideoDecoder.StepCache.Count;

    /// <summary>
    /// Q-0488：后退逐帧的**缓存预填充** —— 只做 seek + 解码并填充滑动窗口，**不渲染**。
    /// 用意：多路（2/3 路）同时需要填充时，各路的解码可以并行（各 Player 的
    /// AVFormatContext / AVCodecContext / AVFrame / Renderer 互相独立，锁也是实例级的），
    /// 而渲染要碰 D3D11 immediate context（非线程安全），必须留在 UI 线程由
    /// <see cref="ShowFramePrev(int)"/> 完成。于是"三路串行各 300ms"变成"并行一次 300ms"。
    ///
    /// 与 ShowFramePrev 差一步：本方法以【当前帧】为填充目标，于是 [target-K, target-1] 落入
    /// 缓存，紧接着 ShowFramePrev 要的正是 target-1 → 命中（0ms 出画）。
    /// 失败不影响主流程 —— ShowFramePrev 会退回它自己的 seek 路径。
    /// </summary>
    /// <param name="step">帧档（1/5/10），需与随后的 ShowFramePrev 保持一致。</param>
    public void PrefillStepCacheForPrev(int step = 1)
    {
        if (!Video.IsOpened || !canPlay || VideoDemuxer.IsHLSLive) return;
        if (VideoDecoder.StepCacheWindow <= 0) return;   // 缓存关闭（K=0 / all-intra）时无意义

        // Q-0537：本方法在**后台线程**执行（宿主用 Task.Run 调度），因此：
        // ① 帧先填进独立的 staging 字典，完成后由 MergeStepCacheFrames 一次性并入 ——
        //    避免后台长时间持有/改写 UI 线程正在查询的窗口；
        // ② keepExistingCache: true —— GetFrame 开头的 Flush() 默认会 DisposeStepCache()，
        //    那会清空 UI 线程正在消费的帧（实测预填充成果被清 → 该步 1948ms）。
        var staging = new Dictionary<long, FlyleafLib.MediaFramework.MediaFrame.VideoFrame>();
        try
        {
            // 目标 = 当前窗口里**最早**的那一帧再往前 step 帧，接着旧窗口继续补；
            // 窗口为空时才用"下一步要显示的帧"。
            // 直接以"当前帧"为目标会与尚未消费的帧抢额度，且当前帧恰为关键帧时（fast seek 落点
            // 就是关键帧）解码路径长度 0，一帧都填不进去 —— 两种情况都会让预填充白跑。
            var oldest = VideoDecoder.StepCacheOldestTs;
            int targetFrame = oldest == AV_NOPTS_VALUE
                ? Math.Max(0, VideoDecoder.GetFrameNumber(CurTime) - step)
                : Math.Max(0, VideoDecoder.GetFrameNumber(oldest) - step);

            var frame = VideoDecoder.GetFrame(targetFrame, true, staging, step, keepExistingCache: true);
            if (frame != null)
                // 目标帧本身也入缓存：ShowFramePrev 随后在 UI 线程命中它并渲染
                // （渲染必须留在 UI 线程，D3D11 immediate context 不是线程安全的）
                staging[frame.Timestamp] = frame;
        }
        catch (Exception ex)
        {
            Log.Error($"[StepCache] prefill failed: {ex.Message}");
        }
        finally
        {
            // 即使失败也要并入已填到的部分（不浪费已付的解码代价）
            VideoDecoder.MergeStepCacheFrames(staging.Values);
        }
    }

    public void SpeedUp() => Speed += Config.Player.SpeedOffset;
    public void SpeedUp2() => Speed += Config.Player.SpeedOffset2;
    public void SpeedDown() => Speed -= Config.Player.SpeedOffset;
    public void SpeedDown2() => Speed -= Config.Player.SpeedOffset2;

    public void FullScreen() => Host?.Player_SetFullScreen(true);
    public void NormalScreen() => Host?.Player_SetFullScreen(false);
    public void ToggleFullScreen()
    {
        if (Host == null)
            return;

        if (Host.Player_GetFullScreen())
            Host.Player_SetFullScreen(false);
        else
            Host.Player_SetFullScreen(true);
    }

    /// <summary>
    /// Starts recording (uses Config.Player.FolderRecordings and default filename title_curTime)
    /// </summary>
    public void StartRecording()
    {
        if (!CanPlay)
            return;
        try
        {
            if (!Directory.Exists(Config.Player.FolderRecordings))
                Directory.CreateDirectory(Config.Player.FolderRecordings);

            string filename = GetValidFileName(string.IsNullOrEmpty(Playlist.Selected.Title) ? "Record" : Playlist.Selected.Title) + $"_{new TimeSpan(CurTime):hhmmss}." + decoder.Extension;
            filename = FindNextAvailableFile(Path.Combine(Config.Player.FolderRecordings, filename));
            StartRecording(ref filename, false);
        }
        catch { }
    }

    /// <summary>
    /// Starts recording
    /// </summary>
    /// <param name="filename">Path of the new recording file</param>
    /// <param name="useRecommendedExtension">You can force the output container's format or use the recommended one to avoid incompatibility</param>
    public void StartRecording(ref string filename, bool useRecommendedExtension = true)
    {
        if (!CanPlay)
            return;

        decoder.StartRecording(ref filename, useRecommendedExtension);
        IsRecording = decoder.IsRecording;
    }

    /// <summary>
    /// Stops recording
    /// </summary>
    public void StopRecording()
    {
        decoder.StopRecording();
        IsRecording = decoder.IsRecording;
    }
    public void ToggleRecording()
    {
        if (!CanPlay) return;

        if (IsRecording)
            StopRecording();
        else
            StartRecording();
    }

    /// <summary>
    /// <para>Saves the current video frame (encoding based on file extention .bmp, .png, .jpg)</para>
    /// <para>If filename not specified will use Config.Player.FolderSnapshots and with default filename title_frameNumber.ext (ext from Config.Player.SnapshotFormat)</para>
    /// <para>If width/height not specified will use the original size. If one of them will be set, the other one will be set based on original ratio</para>
    /// <para>If frame not specified will use the current/last frame</para>
    /// </summary>
    /// <param name="filename">Specify the filename (null: will use Config.Player.FolderSnapshots and with default filename title_frameNumber.ext (ext from Config.Player.SnapshotFormat)</param>
    /// <param name="width">Specify the width (0: will keep the ratio based on height)</param>
    /// <param name="height">Specify the height (0: will keep the ratio based on width)</param>
    /// <exception cref="Exception"></exception>
    public void TakeSnapshotToFile(string filename = null, uint width = 0, uint height = 0)
    {
        if (!CanPlay)
            return;

        if (filename == null)
        {
            try
            {
                if (!Directory.Exists(Config.Player.FolderSnapshots))
                    Directory.CreateDirectory(Config.Player.FolderSnapshots);

                // TBR: if frame is specified we don't know the frame's number
                filename = GetValidFileName(string.IsNullOrEmpty(Playlist.Selected.Title) ? "Snapshot" : Playlist.Selected.Title) + $"_{VideoDecoder.GetFrameNumber(CurTime).ToString()}.{Config.Player.SnapshotFormat}";
                filename = FindNextAvailableFile(Path.Combine(Config.Player.FolderSnapshots, filename));
            }
            catch { return; }
        }

        string ext = GetUrlExtention(filename);

        var imageFormat = ext switch
        {
            "bmp" => ImageFormat.Bmp,
            "png" => ImageFormat.Png,
            "jpg" or "jpeg" => ImageFormat.Jpeg,
            _ => throw new($"Invalid snapshot extention '{ext}' (valid .bmp, .png, .jpeg, .jpg"),
        };

        var snapshotBitmap = Renderer.TakeSnapshot(width, height);
        if (snapshotBitmap == null)
            return;

        try
        {
            snapshotBitmap.Save(filename, imageFormat);
        }
        catch (Exception)
        {
            snapshotBitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// <para>Returns a bitmap of the current or specified video frame</para>
    /// <para>If width/height not specified will use the original size. If one of them will be set, the other one will be set based on original ratio</para>
    /// <para>If frame not specified will use the current/last frame</para>
    /// </summary>
    /// <param name="width">Specify the width (0: will keep the ratio based on height)</param>
    /// <param name="height">Specify the height (0: will keep the ratio based on width)</param>
    /// <returns></returns>
    public System.Drawing.Bitmap TakeSnapshotToBitmap(uint width = 0, uint height = 0) => Renderer?.TakeSnapshot(width, height);

    /// <summary>
    /// <para>Returns a bitmap of the current or specified video frame</para>
    /// <para>If width/height not specified will use the original size. If one of them will be set, the other one will be set based on original ratio</para>
    /// <para>If frame not specified will use the current/last frame</para>
    /// </summary>
    /// <param name="width">Specify the width (0: will keep the ratio based on height)</param>
    /// <param name="height">Specify the height (0: will keep the ratio based on width)</param>
    /// <returns></returns>
    public System.Windows.Media.Imaging.BitmapSource TakeSnapshotToBitmapSource(uint width = 0, uint height = 0) => Renderer?.TakeSnapshotBitmapSource(width, height);

    public void ResetAll()
    {
        ReversePlayback = false;
        Speed = 1;
        Config.Audio.Delay = Config.Subtitles.Delay = 0;
        Config.Video.ResetViewport();
    }
}
