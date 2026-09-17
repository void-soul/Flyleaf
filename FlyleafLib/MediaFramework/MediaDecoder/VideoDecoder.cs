using SharpGen.Runtime;

using FlyleafLib.MediaFramework.MediaDemuxer;
using FlyleafLib.MediaFramework.MediaFrame;
using FlyleafLib.MediaFramework.MediaRemuxer;
using FlyleafLib.MediaFramework.MediaRenderer;
using FlyleafLib.MediaFramework.MediaStream;
using FlyleafLib.MediaPlayer;

namespace FlyleafLib.MediaFramework.MediaDecoder;

/* TBR
 * Missing locks (e.g. GetFrameNext) / Missing checks after locks (e.g. disposed) / Mixing locks (actions/demuxer/codecCtx/renderer)
 *  Currently no issues as we use locks at higher level
 *  
 * GetFrameNumberX: Still issues mainly with Prev, e.g. jumps from 279 to 281 frame | VFR / Timebase / FrameDuration / FPS inaccuracy
 *  Should use just GetFramePrev/Next and work with pts (but we currenlty work with Player.CurTime)
 *  
 * Open/Open2: Merge and review quick Setup/Full Dispose
 */

public unsafe class VideoDecoder : DecoderBase
{
    public Action           OpeningCodec;
    public Renderer         Renderer            { get; private set; }
    public bool             VideoAccelerated    { get; internal set; }

    public VideoStream      VideoStream         => (VideoStream) Stream;

    public long             StartTime           { get; internal set; } = AV_NOPTS_VALUE;
    public long             StartRecordTime     { get; internal set; } = AV_NOPTS_VALUE;

    internal bool           keyPacketRequired;
    internal bool           keyFrameRequired;   // Broken formats even with key packet don't return key frame
    internal bool           isIntraOnly;
    bool                    checkKeyFrame;
    bool                    swFallback;
    long                    startPts;
    long                    lastFixedPts;

    bool                    checkExtraFrames; // DecodeFrameNext
    int                     curFrameWidth, curFrameHeight; // To catch 'codec changed'

    // Hot paths / Same instance
    readonly VideoCache              Frames;
    PacketQueue             vPackets;

    // Q-0427 滑动窗口缓存：帧号 → 解码后的帧。只缓存当前帧【之前】的帧（供逐帧后退取用）；
    // 前进方向不必缓存——解码线程本就在流式预解（Frames 队列）。
    // 缓存的帧会持续持有 HW decoder surface，故 Open 时按窗口大小扩容 extra_hw_frames，
    // 否则缓存会抽干 surface 池、解码线程取不到 surface 而卡死。
    // Q-0431：缓存 key 用【VideoFrame.Timestamp（Player 时间轴）】而不是帧号 ——
    // 帧号有两个口径：GetFrameNumber(CurTime) 含 +demuxer.StartTime、GetFrameNumber2(pts) 不含，
    // 而 demuxer.StartTime 会在 seek 后变化，导致两侧系统性错位（实测偏 229 帧 → 缓存 100% 落空）。
    // Timestamp 与 Player.CurTime 同基准（FillPlanes 里已减掉 Demuxer.StartTime），两侧天然一致。
    internal readonly Dictionary<long, VideoFrame> StepCache = [];
    int                     stepCacheK;

    // Q-0463：HW surface 池的空闲余量（见 Open2 中 extra_hw_frames 的注释）。
    public const int        SurfaceMarginFrames     = 8;

    // Q-0486：ENOMEM（surface 池耗尽）计数。
    // 该错误原本只写进 Flyleaf 自己的日志（宿主日志里完全看不到），现场表现为
    // "画面定格 / 逐帧每步数百毫秒"却无从归因。此处累计计数并暴露给宿主
    // （PlayerGridControl）打显式告警行。解码线程写、UI 线程读 → 用 Interlocked / Volatile。
    int                     enomemCount;
    int                     enomemConsumed;
    long                    lastEnomemTick;

    /// <summary> 自打开以来累计的 ENOMEM 次数（跨线程安全）。 </summary>
    public int EnomemCount => System.Threading.Volatile.Read(ref enomemCount);

    /// <summary> 最近一次 ENOMEM 的 Environment.TickCount64（0 = 从未发生）。 </summary>
    public long LastEnomemTick => System.Threading.Volatile.Read(ref lastEnomemTick);

    /// <summary> 生效的逐帧缓存窗口 K（Open 时快照），供宿主告警时提示。 </summary>
    public int StepCacheWindow => stepCacheK;

    /// <summary>
    /// 取出「自上次调用以来新增的 ENOMEM 次数」并清零。
    /// 宿主据此决定是否打告警行（配合时间节流），避免逐帧连发时每步都刷日志。
    /// </summary>
    public int ConsumeNewEnomemCount()
    {
        var total = System.Threading.Volatile.Read(ref enomemCount);
        var prev = System.Threading.Interlocked.Exchange(ref enomemConsumed, total);
        return total - prev;
    }

    /// <summary> 记录一次 ENOMEM（解码线程调用）。 </summary>
    void NoteEnomem()
    {
        System.Threading.Interlocked.Increment(ref enomemCount);
        System.Threading.Volatile.Write(ref lastEnomemTick, Environment.TickCount64);
    }

    // Q-0427b：all-intra（GOP=1）自动检出 —— 统计每次填充的"路径长度"（seek 后除目标帧外
    // 还能顺带缓存几帧）。GOP=1 时每帧都是关键帧，路径长度恒 ≤1，缓存零收益却白占 HW surface
    // （4K 下 K=20 ≈ +250MB），在 4K120/72GB 这类大流上会压垮解码导致画面不动。
    // 采样若干次后若平均 ≤1 则自动关闭缓存并释放已缓存帧。
    int                     stepCachePathSamples;
    int                     stepCachePathTotal;
    const int               StepCachePathSampleCount = 8;   // 采样 8 次后判定（避免单帧抖动误判）

    // Reverse Playback
    ConcurrentStack<List<nint>>
                            curReverseVideoStack    = [];
    List<nint>              curReverseVideoPackets  = [];
    readonly List<VideoFrame>        curReverseVideoFrames   = [];
    int                     curReversePacketPos     = 0;

    // Drop frames if FPS is higher than allowed
    int                     curSpeedFrame           = 9999; // don't skip first frame (on start/after seek-flush)
    double                  skipSpeedFrames         = 0;

    // Fixes Seek Backwards failure on broken formats
    long                    curFixSeekDelta         = 0;
    const long              FIX_SEEK_DELTA_MCS      = 2_100_000;

    // Q-0431：FIX_SEEK_DELTA_MCS 是【一次 2.1 秒】的粗粒度回退（为 seek backward 失败的格式设计），
    // 而 curFixSeekDelta 原实现只增不减 —— 只要触发过一次，此后每一次 seek 都被永久提前 2.1 秒：
    // 解码路径从 ≤1 个 GOP 膨胀到数百帧（实测 252 帧），既慢又让滑动窗口缓存收不到有用的尾部帧。
    // MaxLeadFrames = seek 落点允许比目标早的最大帧数（正常回退最多 1 个 GOP，留余量取 20）；
    // 超出的部分按比例回收 curFixSeekDelta，使其能自愈。
    const int               MaxLeadFrames           = 20;

    // Q-0461：精确 seek（"回退到目标前一个 IDR + 解码前进 + 丢弃早于目标的帧"）。
    // 背景：mpegts + HEVC 的 av_seek_frame(BACKWARD) 落点在【目标所在 GOP 的内部】
    // （实测标准文件 -ss 落在该 GOP 的 IDR+11 帧处），目标之前那个 IDR 已被越过；
    // 而解码器现有的 keyPacketRequired 是"丢弃非关键包直到【下一个】IDR"，那个 IDR
    // 必定晚于目标 —— 于是每次 seek 都过冲最多 1 个 GOP，且多机位因 GOP 相位不同
    // （实测两份样本开头孤儿帧分别为 96 / 112）而互相错开最多 1 个 GOP。
    // 解法：seek 时多回退一个 GOP，保证落点之后的第一个 IDR <= 目标；再丢弃时间戳
    // 早于目标的帧，使所有机位精确落在同一时刻 T（与 GOP 相位无关）。
    long                    accurateSeekTargetTs    = AV_NOPTS_VALUE; // 相对 demuxer.StartTime 的 ticks(100ns)
    int                     accurateSeekDropped;
    int                     maxAccurateSeekDrops    = 60;   // 由 SetAccurateSeekTarget 按时间预算换算

    // Q-0461：本类【不】自己测 GOP —— 用 Demuxer.MeasuredGopTicks（Q-0458 已实现，
    // 且在 Demuxer.Seek 里把 lastKeyPacketPts 置 NoTs 以避免跨越跳转算出假 GOP）。
    // 这里曾有一份重复实现（取相邻关键帧 pts 间隔的最大值），但没在 seek 时重置采样点：
    // 从 2.0s 播放位置跳到末尾后，下一个关键帧在 86.0s → 差值 84s 被当成 GOP，
    // 于是每次回退 seek 都减 84 秒、被 clamp 到文件开头，落点归零。
    internal void SetAccurateSeekTarget(long targetTs, long maxLeadTicks)
    {
        accurateSeekTargetTs    = targetTs;
        accurateSeekDropped     = 0;

        // 丢弃预算必须是【时间】而非帧数：正常只需 <= 1 个 GOP；给 4 倍余量并保底 2 秒。
        // 之前写死 400 帧，在 120fps 是 3.3s、在 30fps 却是 13.3s —— 帧率一变就失控。
        long frameTicks = VideoStream != null ? VideoStream.FrameDuration : 0;
        maxAccurateSeekDrops = frameTicks > 0
            ? (int)Math.Clamp(maxLeadTicks / frameTicks, 1, 2000)
            : 60;
    }

    public VideoDecoder(Config config, int uniqueId = -1, bool createRenderer = true, Player player = null) : base(config, uniqueId)
    {
        getHWformat = new(GetFormat);

        if (createRenderer)
        {
            Renderer= new(this, UniqueId, player);
            Frames  = Renderer.Frames;
        }
    }

    #region Video Acceleration (Should be disposed seperately)
    public CodecSpec CurCodecSpec;
    readonly AVCodecContext_get_format getHWformat;

    internal const AVPixelFormat    HW_PIX_FMT  = AVPixelFormat.D3d11;
    internal const AVHWDeviceType   HW_DEVICE   = AVHWDeviceType.D3d11va;

    public class CodecSpec
    {
        public string   Name;
        public AVCodec* Codec;
        public bool     IsHW;
        public bool     IsEmpty => Codec == null;

        internal static CodecSpec Empty = new();
    }
    static readonly ConcurrentDictionary<AVCodecID,  CodecSpec> hwSpecs  = [];
    static readonly ConcurrentDictionary<AVCodecID,  CodecSpec> swSpecs  = [];
    static readonly ConcurrentDictionary<string,     CodecSpec> specs    = [];
    static CodecSpec FindHWDecoder(AVCodecID id)
    {
        if (hwSpecs.TryGetValue(id, out CodecSpec spec))
            return spec;

        AVCodec* codec, found = null;
        void* opaque = null;
        while ((codec = av_codec_iterate(ref opaque)) != null)
        {
            if (codec->id != id || av_codec_is_decoder(codec) == 0)
                continue;

            int i = 0;
            AVCodecHWConfig* config;
            while((config = avcodec_get_hw_config(codec, i++)) != null)
                if (config->pix_fmt == HW_PIX_FMT && config->methods.HasFlag(AVCodecHwConfigMethod.HwDeviceCtx))
                {
                    spec = new() { Codec = codec, Name = BytePtrToStringUTF8(codec->name), IsHW = true};
                    hwSpecs[codec->id] = spec;
                    return spec;
                }
        }

        hwSpecs[id] = CodecSpec.Empty;
        return CodecSpec.Empty;
    }
    static CodecSpec FindSWDecoder(AVCodecID id)
    {
        if (swSpecs.TryGetValue(id, out CodecSpec spec))
            return spec;

        AVCodec* codec = avcodec_find_decoder(id);
        spec = codec != null ? new() { Codec = codec, Name = BytePtrToStringUTF8(codec->name) } : CodecSpec.Empty;
        swSpecs[codec->id] = spec;
        return spec;
    }
    static CodecSpec FindDecoder(string name)
    {
        if (specs.TryGetValue(name, out CodecSpec spec))
            return spec;

        AVCodec* codec = avcodec_find_decoder_by_name(name);
        if (codec == null)
        {
            specs[name] = CodecSpec.Empty;
            return CodecSpec.Empty;
        }

        bool isHW = false;
        int i = 0;
        AVCodecHWConfig* config;
        while((config = avcodec_get_hw_config(codec, i++)) != null)
            if (config->pix_fmt == HW_PIX_FMT && config->methods.HasFlag(AVCodecHwConfigMethod.HwDeviceCtx))
            {
                isHW = true;
                break;
            }

        spec = new() { Codec = codec, Name = BytePtrToStringUTF8(codec->name), IsHW = isHW };
        specs[name] = spec;
        return spec;
    }

    private AVPixelFormat GetFormat(AVCodecContext* avctx, AVPixelFormat* pix_fmts)
    {
        if (CanDebug)
        {
            Log.Debug($"Codec profile '{avcodec_profile_name(codecCtx->codec_id, codecCtx->profile)}'");

            if (CanTrace)
            {
                var save = pix_fmts;
                while (*pix_fmts != AVPixelFormat.None)
                {
                    Log.Trace($"{*pix_fmts}");
                    pix_fmts++;
                }
                pix_fmts = save;
            }
        }

        bool foundHWformat = false;

        while (*pix_fmts != AVPixelFormat.None)
        {
            if ((*pix_fmts) == HW_PIX_FMT)
            {
                foundHWformat = true;
                break;
            }

            pix_fmts++;
        }

        if (codecCtx->hw_frames_ctx != null)
            av_buffer_unref(&codecCtx->hw_frames_ctx);

        if (!foundHWformat || !Renderer.ConfigHWFrames())
        {
            Log.Info("HW decoding failed");
            swFallback = true;
            return avcodec_default_get_format(avctx, pix_fmts);
        }

        codecCtx->hw_frames_ctx = av_buffer_ref(Renderer.ffFrames);


        return HW_PIX_FMT;
    }
    #endregion

    protected override bool Setup()
    {
        if (Renderer.Disposed)
            Renderer.Setup();

        VideoAccelerated = !swFallback && Renderer.ffDevice != null && Config.Video.VideoAcceleration;

        OpeningCodec?.Invoke();

        if (!string.IsNullOrEmpty(Config.Decoder._VideoCodec))
            CurCodecSpec = FindDecoder(Config.Decoder._VideoCodec);
        else if (VideoAccelerated)
        {
            CurCodecSpec = FindHWDecoder(Stream.CodecID);
            if (CurCodecSpec.IsEmpty)
            {
                if (CanDebug) Log.Debug($"HW decoding not supported for {Stream.CodecID}");
                CurCodecSpec = FindSWDecoder(Stream.CodecID);
            }
        }
        else
            CurCodecSpec = FindSWDecoder(Stream.CodecID);

        if (CurCodecSpec.IsEmpty)
        {
            Log.Error($"Decoder not found ({(!string.IsNullOrEmpty(Config.Decoder._VideoCodec) ? Config.Decoder._VideoCodec : Stream.CodecID)})");
            return false;
        }

        codecCtx = avcodec_alloc_context3(CurCodecSpec.Codec); // Pass codec to use default settings
        if (codecCtx == null)
        {
            Log.Error($"Failed to allocate context");
            return false;
        }

        int ret = avcodec_parameters_to_context(codecCtx, Stream.AVStream->codecpar);
        if (ret < 0)
        {
            Log.Error($"Failed to pass parameters to context - {FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");
            return false;
        }

        codecCtx->pkt_timebase  = Stream.AVStream->time_base;
        codecCtx->codec_id      = CurCodecSpec.Codec->id; // avcodec_parameters_to_context will change this we need to set Stream's Codec Id (eg we change mp2 to mp3)
        codecCtx->apply_cropping= 0;

        if (Config.Decoder.ShowCorrupted)
            codecCtx->flags |= CodecFlags.OutputCorrupt;

        if (Config.Decoder.LowDelay)
        {
            if (Config.Decoder.AllowDropFrames)
                codecCtx->flags |= CodecFlags.LowDelay;
            else
            {
                codecCtx->skip_frame = AVDiscard.None;
                codecCtx->flags2 |= CodecFlags2.Fast;
            }
        }
        else if (!Config.Decoder.AllowDropFrames)
            codecCtx->skip_frame = AVDiscard.None;

        var codecOpts = Config.Decoder.VideoCodecOpt;
        AVDictionary* avopt = null;
        foreach(var optKV in codecOpts)
            _ = av_dict_set(&avopt, optKV.Key, optKV.Value, 0);

        VideoAccelerated = VideoAccelerated && CurCodecSpec.IsHW;

        if (VideoAccelerated)
        {
            /* TODO: Frame threading [codecCtx->thread_type = ThreadTypeFlags.Frame]
             * Possible requires patching FFmpeg to pass BindFlags.ShaderResource to new allocated textures (get_buffer maybe?)
             * Seems to work fine with D3D11VP (if we pass the right texture from frame->data[0])
             */

            codecCtx->thread_count      = 1;
            codecCtx->hwaccel_flags    |= HWAccelFlags.IgnoreLevel;
            if (Config.Decoder.AllowProfileMismatch)
                codecCtx->hwaccel_flags|= HWAccelFlags.AllowProfileMismatch;
            codecCtx->get_format        = getHWformat;
            codecCtx->hw_device_ctx     = av_buffer_ref(Renderer.ffDevice);
            // Q-0427：+1 for Renderer's LastFrame；再 +FrameCacheWindow，因为滑动窗口缓存的帧
            // 会长期持有 surface（不缓存则会抽干池 → 解码线程卡死）。代价是显存按窗口线性增长。
            // Q-0463：池还要【留余量】。池容量 = MaxVideoFrames + 1 + K 时，同时持有者恰好是
            // VideoCache(MaxVideoFrames) + Renderer(1) + StepCache(K) —— 三者打满即 100% 占用，
            // 解码器自己（DPB / 参考帧 / 在途帧）拿到 0 个空闲 surface → avcodec 返回
            // ENOMEM(-12)（实测 4K120 / K=119 时 12462 次，紧接着 Too many errors 停摆）。
            // 故额外给出 SurfaceMarginFrames 个空闲 surface。
            codecCtx->extra_hw_frames   = Config.Decoder.MaxVideoFrames + 1
                                        + Math.Max(0, Config.Decoder.FrameCacheWindow)
                                        + SurfaceMarginFrames;
        }
        else
            codecCtx->thread_count      = Math.Min(Config.Decoder.VideoThreads, codecCtx->codec_id == AVCodecID.Hevc ? 32 : 16);

        ret = avcodec_open2(codecCtx, null, avopt == null ? null : &avopt);
        if (ret < 0)
        {
            if (avopt != null) av_dict_free(&avopt);
            Log.Error($"Failed to open codec - {FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");
            return false;
        }

        if (avopt != null)
        {
            AVDictionaryEntry *t = null;
            while ((t = av_dict_get(avopt, "", t, DictReadFlags.IgnoreSuffix)) != null)
                Log.Debug($"Ignoring codec option {BytePtrToStringUTF8(t->key)}");

            av_dict_free(&avopt);
        }

        if (codecCtx->codec_descriptor != null)
            isIntraOnly = codecCtx->codec_descriptor->props.HasFlag(CodecPropFlags.IntraOnly);

        vPackets            = demuxer.VideoPackets;
        // Q-0427：窗口半径在 Open 时快照——它决定了 extra_hw_frames（surface 池），
        // 运行中改配置不会重建解码器，故这里取一次即可。
        stepCacheK          = Math.Max(0, Config.Decoder.FrameCacheWindow);
        keyFrameRequired    = keyPacketRequired = false; // allow no key packet after open (lot of videos missing this)
        filledFromCodec     = false;
        isDraining          = false;
        lastFixedPts        = 0; // TBR: might need to set this to first known pts/dts
        startPts            = VideoStream.StartTimePts;
        allowedErrors       = Config.Decoder.MaxErrors;

        // Not all codecs fill key frame flag | https://github.com/SuRGeoNix/Flyleaf/issues/638 | Old MOV/MP4 container marking packets loosely as key
        checkKeyFrame       = codecCtx->codec_id != AVCodecID.Av1 &&
                             (VideoAccelerated ||
                              codecCtx->codec_id != AVCodecID.Vp8 && codecCtx->codec_id != AVCodecID.Vp9 && codecCtx->codec_id != AVCodecID.Qtrle);

        if (CanDebug) Log.Debug($"Using {CurCodecSpec.Name} {(VideoAccelerated ? "(HW)" : "(SW)")}");

        return true;
    }

    /// <summary>
    /// Q-0537：滑动窗口缓存的访问锁。后台预填充（另一线程）合并新帧 与 UI 线程查询/消费缓存
    /// 必须互斥 —— Dictionary 并发读写会损坏结构。锁内只有字典操作（µs 级），不构成阻塞点。
    /// </summary>
    readonly object stepCacheLock = new();

    /// <summary>
    /// Q-0537：把一批帧合并进滑动窗口（后台预填充完成后调用）。按 <see cref="CacheStepFrame"/> 规则：
    /// 同键替换、超限淘汰时间戳最小的一帧；窗口已关闭则直接释放，避免泄漏。
    /// </summary>
    public void MergeStepCacheFrames(IEnumerable<VideoFrame> frames)
    {
        if (frames == null) return;

        lock (stepCacheLock)
        {
            foreach (var f in frames)
            {
                if (f == null) continue;
                if (stepCacheK <= 0) { f.Dispose(); continue; }
                CacheStepFrame(StepCache, f.Timestamp, f);
            }
        }
    }

    internal void Flush() => Flush(true);

    /// <param name="disposeStepCache">
    /// Q-0537：是否清空滑动窗口缓存。默认 true（seek 后旧帧号失效，必须释放）。
    /// 后台预填充传 false：它往**更早**的方向填，UI 线程正在消费的帧依然有效，
    /// 清掉会让 UI 那一步必然 MISS（实测预填充成果被清 → 该步 1948ms）。
    /// </param>
    internal void Flush(bool disposeStepCache)
    {
        lock (lockActions)
            lock (lockCodecCtx)
            {
                if (Disposed)
                    return;

                if (Status == Status.Ended)
                    Status = Status.Stopped;

                DisposeFrames();
                if (disposeStepCache)
                    DisposeStepCache(); // Q-0427：seek/flush 后旧缓存的帧号已失效，必须释放（否则泄漏 HW surface）
                avcodec_flush_buffers(codecCtx);

                isDraining             = false;
                keyFrameRequired       = false;
                keyPacketRequired      = !isIntraOnly;
                StartTime              = AV_NOPTS_VALUE;
                curSpeedFrame          = 9999;
                // Q-0461：每次 Flush 都开启新一轮解码前进，旧的丢弃目标必须作废
                accurateSeekTargetTs   = AV_NOPTS_VALUE;
                accurateSeekDropped    = 0;
            }
    }

    #region Run Loop
    int allowedErrors;
    bool isDraining;
    protected override void RunInternal()
    {
        // Q-0463：恢复播放即释放逐帧滑动窗口缓存。
        // 逐帧后退时解码器是暂停的，缓存里的 K 帧持续持有 HW surface；此前只有
        // seek（Flush）或停止（Dispose）才释放，导致"逐帧完继续播放"期间一直白占
        // K × 单帧（4K 约 12.4MB/帧）显存 —— 实测就是"显存一直涨、停止播放才掉"。
        // 起播说明已离开逐帧上下文，旧窗口（围绕旧位置）也不可能再命中。
        // 与 UI 线程共用 lockActions：ShowFramePrev 的缓存读写在同一把锁内。
        if (StepCache.Count > 0)
            lock (lockActions)
                if (StepCache.Count > 0)
                {
                    if (CanDebug) Log.Debug($"[StepCache] playback resumed -> release {StepCache.Count} cached frames");
                    DisposeStepCache();
                }

        if (demuxer.IsReversePlayback)
        {
            RunInternalReverse();
            return;
        }

        int sleepMs = Config.Player.MaxLatency == 0 ? 10 : 2;
        int ret;
        AVPacket *packet;

        do
        {
            // Wait until Queue not Full or Stopped
            if (Frames.Count >= Config.Decoder.MaxVideoFrames)
            {
                lock (lockStatus)
                    if (Status == Status.Running) Status = Status.QueueFull;

                while (Frames.Count >= Config.Decoder.MaxVideoFrames && Status == Status.QueueFull)
                    Thread.Sleep(sleepMs);

                lock (lockStatus)
                {
                    if (Status != Status.QueueFull) break;
                    Status = Status.Running;
                }
            }

            // While Packets Queue Empty (Drain | Quit if Demuxer stopped | Wait until we get packets)
            if (vPackets.IsEmpty && !isDraining)
            {
                CriticalArea = true;

                lock (lockStatus)
                    if (Status == Status.Running) Status = Status.QueueEmpty;

                while (vPackets.IsEmpty && Status == Status.QueueEmpty)
                {
                    if (demuxer.Status == Status.Ended)
                    {
                        lock (lockStatus)
                        {
                            Log.Debug("Draining");
                            isDraining          = true;
                            var drainPacket     = av_packet_alloc();
                            drainPacket->data   = null;
                            drainPacket->size   = 0;
                            vPackets.Enqueue(drainPacket);
                        }

                        break;
                    }
                    else if (!demuxer.IsRunning)
                    {
                        if (CanDebug) Log.Debug($"Demuxer is not running [Demuxer Status: {demuxer.Status}]");

                        int retries = 5;

                        while (retries > 0)
                        {
                            retries--;
                            Thread.Sleep(10);
                            if (demuxer.IsRunning) break;
                        }

                        lock (demuxer.lockStatus)
                        lock (lockStatus)
                        {
                            if (demuxer.Status == Status.Pausing || demuxer.Status == Status.Paused)
                                Status = Status.Pausing;
                            else if (demuxer.Status != Status.Ended)
                                Status = Status.Stopping;
                            else
                                continue;
                        }

                        break;
                    }

                    Thread.Sleep(sleepMs);
                }

                lock (lockStatus)
                {
                    CriticalArea = false;
                    if (Status != Status.QueueEmpty) break;
                    Status = Status.Running;
                }
            }

            // RecvFrame | GetPacket | SendPacket
            lock (lockCodecCtx)
            {
                if (Status == Status.Stopped)
                    continue;

                if (!keyPacketRequired)
                {
                    ret = RecvAVFrame();
                    if (ret == 0)
                    {
                        if (FillEnqueueAVFrame() == -1234)
                        {
                            Status = Status.Stopping;
                            break;
                        }

                        continue;
                    }
                    else if (ret != AVERROR_EAGAIN)
                    {
                        if (ret == -1234)
                            Status = Status.Stopping;

                        break; // else EOF
                    }
                }
                
                packet = vPackets.Dequeue();

                if (packet == null)
                    continue;

                if (isRecording)
                {
                    if (!recKeyPacketRequired && (packet->flags & PktFlags.Key) != 0)
                    {
                        recKeyPacketRequired = true;
                        StartRecordTime = (long)(packet->pts * VideoStream.Timebase) - demuxer.StartTime;
                    }

                    if (recKeyPacketRequired)
                        curRecorder.Write(av_packet_clone(packet));
                }

                ret = SendAVPacket(packet);
                if (ret != 0)
                {
                    if (ret == AVERROR_EAGAIN)
                    {   // Fast retry => Legitimate decoding errors | Waiting for key packet
                        while (Status == Status.Running && ret == AVERROR_EAGAIN && (packet = vPackets.Dequeue()) != null)
                            ret = SendAVPacket(packet); // TBR: Should record those?

                        if (ret == 0 || packet == null)
                            continue;
                    }

                    if (ret == -1234)
                        Status = Status.Stopping;

                    break; // else EOF
                }
            }

        } while (Status == Status.Running);

        if (isRecording)
        {
            StopRecording();
            recCompleted(MediaType.Video);
        }
    }
    internal int SendAVPacket(AVPacket* packet)
    {   /* Sends the provided packet to the decoder (avcodec_send_packet) | Should be used as tied to Run Loop (vPackets[] / Frames[])
         * - Key Packet / Frame Validations
         * - Software Fallback
         * - Global Errors Counter
         * 
         * Returns
         *  0       : Call RecvAVFrame  (Success | HasMoreOutput)   * Ideally we should not dipose packet when more output (but should not happen with current design)
         *  EAGAIN  : Call SendAVPacket (Ignored)                   * Invalid Packet, send next one
         *  EOF     : Quit Loop
         *  -1234   : Quit Loop         (Critical)                  * E.g. Status = Stopping
         */

        if (keyPacketRequired)
        {
            if (!packet->flags.HasFlag(PktFlags.Key) && packet->pts != startPts)
            {   // https://trac.ffmpeg.org/ticket/9412 | HEVC fails to seek at key packet (Fixed?) | Don't treat as error (?)
                if (CanDebug) Log.Debug("Ignoring non-key packet");
                av_packet_free(&packet);
                return AVERROR_EAGAIN;
                
            }

            keyFrameRequired  = checkKeyFrame && packet->pts != startPts;
            keyPacketRequired = false;
        }

        // TBR: AVERROR(EAGAIN) ideally we keep the packet and resend it after recv (it shouldn't happen at all as we keep track)
        int ret = avcodec_send_packet(codecCtx, packet);

        if (swFallback)
        {   // Could happen during (VA) GetFormat (called by avcodec_send_packet)
            SWFallback();
            ret = avcodec_send_packet(codecCtx, packet);
        }

        av_packet_free(&packet);

        if (ret == 0 || ret == AVERROR_EAGAIN)
            return 0;

        // TBR: Possible check for VA failed here (normally this will happen during get_format)

        if (ret == AVERROR_EOF)
        {
            if (!vPackets.IsEmpty) { avcodec_flush_buffers(codecCtx); return AVERROR_EAGAIN; } // TBR: Happens on HLS while switching video streams
            Status = Status.Ended;
            return AVERROR_EOF;
        }

        if (ret == AVERROR_ENOMEM) { NoteEnomem(); Log.Error($"{FFmpegEngine.ErrorCodeToMsg(ret)}"); return -1234; }

        allowedErrors--;
        if (CanWarn) Log.Warn($"{FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");

        if (allowedErrors == 0) { Log.Error("Too many errors!"); return -1234; }

        return AVERROR_EAGAIN;
    }
    internal int RecvAVFrame()
    {   /* Receives frame from the decoder (avcodec_receive_frame) | Should be used as tied to Run Loop (vPackets[] / Frames[])
         * - Key Frame Validation
         * - Codec Change
         * - Fix Timestamps
         * - Fill Stream From Codec
         * - Skip Frames
         * - Global Errors Counter
         * 
         * Returns
         *  0       : Call RecvAVFrame  (Success)           * Try for more output
         *  EAGAIN  : Call SendAVPacket (NeedsMoreInput)    * Invalid Packet, send next one
         *  EOF     : Quit Loop         (Ended)             * Drained
         *  -1234   : Quit Loop         (Critical)          * E.g. Status = Stopping
         */
        int ret = avcodec_receive_frame(codecCtx, frame);
        if (ret != 0)
        {
            if (ret == AVERROR_EAGAIN)
                return AVERROR_EAGAIN;

            if (ret == AVERROR_EOF)
            {
                if (!vPackets.IsEmpty) { avcodec_flush_buffers(codecCtx); return AVERROR_EAGAIN; } // TBR: Happens on HLS while switching video streams
                Status = Status.Ended;
                return AVERROR_EOF;
            }

            if (ret == AVERROR_ENOMEM || ret == AVERROR_EINVAL)
            {
                // Q-0486：只对 ENOMEM 计数（EINVAL 多为码流损坏，与显存无关）
                if (ret == AVERROR_ENOMEM) NoteEnomem();
                Log.Error($"{FFmpegEngine.ErrorCodeToMsg(ret)}"); return -1234;
            }

            allowedErrors--;
            if (CanWarn) Log.Warn($"{FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");

            if (allowedErrors == 0) { Log.Error("Too many errors!"); return -1234; }

            return RecvAVFrame(); // TBR maybe try another packet EAGAIN
        }

        if (keyFrameRequired)
        {
            if (!frame->flags.HasFlag(FrameFlags.Key))
            {
                if (CanInfo) Log.Info("Ignoring non-key frame");
                av_frame_unref(frame);
                return RecvAVFrame();
            }
            
            keyFrameRequired = false;
        }

        if ((frame->height != curFrameHeight || frame->width != curFrameWidth) && filledFromCodec)
        {
            filledFromCodec = false;
            Log.Warn($"Codec changed {VideoStream.CodecID} {curFrameWidth}x{curFrameHeight} => {codecCtx->codec_id} {frame->width}x{frame->height}");
        }

        if (frame->best_effort_timestamp != AV_NOPTS_VALUE)
            frame->pts = frame->best_effort_timestamp;

        else if (frame->pts == AV_NOPTS_VALUE)
        {
            if (!VideoStream.FixTimestamps && VideoStream.Duration > TimeSpan.FromSeconds(1).Ticks)
            {
                // TBR: it is possible to have a single frame / image with no dts/pts which actually means pts = 0 ? (ticket_3449.264) - GenPts will not affect it
                // TBR: first frame might no have dts/pts which probably means pts = 0 (and not start time!)
                av_frame_unref(frame);
                return RecvAVFrame();
            }

            // Create timestamps for h264/hevc raw streams (Needs also to handle this with the remuxer / no recording currently supported!)
            frame->pts = lastFixedPts + VideoStream.StartTimePts;
            lastFixedPts += av_rescale_q(VideoStream.FrameDuration / 10, Engine.FFmpeg.AV_TIMEBASE_Q, VideoStream.AVStream->time_base);
        }

        if (!filledFromCodec) // Ensures we have a proper frame before filling from codec
        {
            ret = FillFromCodec(frame);
            if (ret == -1234)
                return -1234;
        }

        // Q-0461：精确 seek 的解码前进阶段——丢弃时间戳早于目标的帧（见字段注释）。
        // 必须放在 FillFromCodec 之后：首次解码依赖它刷新 PixelFormat / StartTimePts。
        // 必须放在 FillEnqueueAVFrame（FillPlanes）之前：此时尚未分配 GPU 纹理，丢弃零成本。
        if (accurateSeekTargetTs != AV_NOPTS_VALUE)
        {
            long ts = (long)(frame->pts * VideoStream.Timebase) - demuxer.StartTime;

            if (ts < accurateSeekTargetTs && accurateSeekDropped < maxAccurateSeekDrops)
            {
                // Q-0461：第一行丢弃日志是判断 seek 是否正常的决定性证据——
                // 落点应该只比目标早不到 1 个 GOP；若早了几秒甚至几十秒，说明
                // demuxer 的 seek 落点本身就不对（此时丢弃只会把画面推得更远）。
                if (accurateSeekDropped == 0 && CanDebug)
                    Log.Debug($"[Q-0461] Accurate seek: landing={TicksToTime(ts)} target={TicksToTime(accurateSeekTargetTs)} " +
                              $"lead={TicksToTime(accurateSeekTargetTs - ts)} (maxDrops={maxAccurateSeekDrops})");

                accurateSeekDropped++;
                av_frame_unref(frame);
                return RecvAVFrame();
            }

            if (ts < accurateSeekTargetTs && CanWarn)
                Log.Warn($"[Q-0461] Accurate seek target not reached after {accurateSeekDropped} frames, presenting anyway");

            accurateSeekTargetTs = AV_NOPTS_VALUE;
        }

        if (skipSpeedFrames > 1)
        {
            curSpeedFrame++;
            if (curSpeedFrame < skipSpeedFrames)
            {
                av_frame_unref(frame);
                return RecvAVFrame();
            }
            curSpeedFrame = 0;
        }

        return 0;
    }
    VideoFrame FillAVFrame()
    {   // Renderer.FillPlanes with error handling
        VideoFrame mFrame = null;

        try
        {
            mFrame = Renderer.FillPlanes(ref frame);
        }
        catch(SharpGenException e)
        {
            Log.Error($"FillAVFrame failed ({e.ResultCode.NativeApiCode} | {Renderer.Device.DeviceRemovedReason.NativeApiCode} | {e.Message})");
            ResetLocal();
        }
        catch (Exception ex)
        {
            Log.Error($"FillAVFrame failed ({ex.Message})");
            av_frame_unref(frame);
        }

        return mFrame;
    }
    internal int FillEnqueueAVFrame()
    {
        VideoFrame mFrame = FillAVFrame();

        if (mFrame != null)
        {
            allowedErrors = Config.Decoder.MaxErrors;

            if (StartTime == NoTs)
                StartTime = mFrame.Timestamp;

            Frames.Enqueue(mFrame);

            return 0;
        }

        allowedErrors--;
        if (allowedErrors == 0) { Log.Error("Too many errors!"); return -1234; }

        return AVERROR_EAGAIN; // currently same as 0
    }
    void ResetLocal()
    {   // Silent Dispose + Renderer Reset + Reopen (TBR: locks / can't pasuse player from here)
        DisposeInternal();
        if (codecCtx != null)
        {
            fixed (AVCodecContext** ptr = &codecCtx)
                avcodec_free_context(ptr);

            codecCtx = null;
        }
        Renderer.Reset(pausePlayer: false, fromDecoder: true);
        Open2(Stream, null, false);
        keyPacketRequired   = !isIntraOnly;
        keyFrameRequired    = false;
    }
    #endregion

    internal int FillFromCodec(AVFrame* frame)
    {
        filledFromCodec = true;
        curFixSeekDelta = 0;
        curFrameWidth   = frame->width;
        curFrameHeight  = frame->height;

        VideoStream.Refresh(this, frame);
        startPts        = VideoStream.StartTimePts;
        skipSpeedFrames = speed * VideoStream.FPS / (Config.Video.MaxOutputFps + 1);

        int ret = 0;

        if (VideoStream.PixelFormat == AVPixelFormat.None)
        {
            Log.Error("PixelFormat unknown");
            ret = -1234;
        }
        else
        {
            try
            {
                Renderer.VPConfig(VideoStream, frame);
            }
            catch (Exception ex)
            {
                Log.Error($"VPConfig failed ({ex.Message})");
                ret = -1234;
            }
        }

        CodecChanged?.Invoke(this);

        return ret;
    }

    internal bool SWFallback()
    {
        bool ret;

        DisposeInternal();
        if (codecCtx != null)
            fixed (AVCodecContext** ptr = &codecCtx)
                avcodec_free_context(ptr);

        codecCtx            = null;
        swFallback          = true;
        bool keyRequiredOld = keyPacketRequired;
        ret = Open2(Stream, null, false); // TBR:  Dispose() on failure could cause a deadlock
        keyPacketRequired   = keyRequiredOld;
        keyFrameRequired    = false;
        swFallback          = false;
        filledFromCodec     = false;

        return ret;
    }

    private void RunInternalReverse()
    {   // BUG: with B-frames, we should not remove the ref packets (we miss frames each time we restart decoding the gop)
        int ret = 0;
        int allowedErrors = Config.Decoder.MaxErrors;
        AVPacket *packet;

        do
        {
            // While Packets Queue Empty (Drain | Quit if Demuxer stopped | Wait until we get packets)
            if (demuxer.VideoPacketsReverse.IsEmpty && curReverseVideoStack.IsEmpty && curReverseVideoPackets.Count == 0)
            {
                CriticalArea = true;

                lock (lockStatus)
                    if (Status == Status.Running) Status = Status.QueueEmpty;

                while (demuxer.VideoPacketsReverse.IsEmpty && Status == Status.QueueEmpty)
                {
                    if (demuxer.Status == Status.Ended) // TODO
                    {
                        lock (lockStatus) Status = Status.Ended;

                        break;
                    }
                    else if (!demuxer.IsRunning)
                    {
                        if (CanDebug) Log.Debug($"Demuxer is not running [Demuxer Status: {demuxer.Status}]");

                        int retries = 5;

                        while (retries > 0)
                        {
                            retries--;
                            Thread.Sleep(10);
                            if (demuxer.IsRunning) break;
                        }

                        lock (demuxer.lockStatus)
                        lock (lockStatus)
                        {
                            if (demuxer.Status == Status.Pausing || demuxer.Status == Status.Paused)
                                Status = Status.Pausing;
                            else if (demuxer.Status != Status.Ended)
                                Status = Status.Stopping;
                            else
                                continue;
                        }

                        break;
                    }

                    Thread.Sleep(20);
                }

                lock (lockStatus)
                {
                    CriticalArea = false;
                    if (Status != Status.QueueEmpty) break;
                    Status = Status.Running;
                }
            }

            if (curReverseVideoPackets.Count == 0)
            {
                if (curReverseVideoStack.IsEmpty)
                    demuxer.VideoPacketsReverse.TryDequeue(out curReverseVideoStack);

                curReverseVideoStack.TryPop(out curReverseVideoPackets);
                curReversePacketPos = 0;
            }

            while (curReverseVideoPackets.Count > 0 && Status == Status.Running)
            {
                // Wait until Queue not Full or Stopped
                if (Frames.Count + curReverseVideoFrames.Count >= Config.Decoder.MaxVideoFrames)
                {
                    lock (lockStatus)
                        if (Status == Status.Running) Status = Status.QueueFull;

                    while (Frames.Count + curReverseVideoFrames.Count >= Config.Decoder.MaxVideoFrames && Status == Status.QueueFull) Thread.Sleep(20);

                    lock (lockStatus)
                    {
                        if (Status != Status.QueueFull) break;
                        Status = Status.Running;
                    }
                }

                lock (lockCodecCtx)
                {
                    if (keyPacketRequired)
                    {
                        keyPacketRequired = false;
                        curReversePacketPos = 0;
                        break;
                    }

                    packet = (AVPacket*)curReverseVideoPackets[curReversePacketPos++];
                    ret = avcodec_send_packet(codecCtx, packet);

                    if (ret != 0 && ret != AVERROR(EAGAIN))
                    {
                        if (ret == AVERROR_EOF) { Status = Status.Ended; break; }

                        if (CanWarn) Log.Warn($"{FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");

                        allowedErrors--;
                        if (allowedErrors == 0) { Log.Error("Too many errors!"); Status = Status.Stopping; break; }

                        for (int i = curReverseVideoPackets.Count - 1; i >= curReversePacketPos - 1; i--)
                        {
                            packet = (AVPacket*)curReverseVideoPackets[i];
                            av_packet_free(&packet);
                            curReverseVideoPackets[curReversePacketPos - 1] = 0;
                            curReverseVideoPackets.RemoveAt(i);
                        }

                        avcodec_flush_buffers(codecCtx);
                        curReversePacketPos = 0;

                        for (int i = curReverseVideoFrames.Count - 1; i >= 0; i--)
                            Frames.Enqueue(curReverseVideoFrames[i]);

                        curReverseVideoFrames.Clear();

                        continue;
                    }

                    while (true)
                    {
                        ret = avcodec_receive_frame(codecCtx, frame);
                        if (ret != 0) { av_frame_unref(frame); break; }

                        if (frame->best_effort_timestamp != AV_NOPTS_VALUE)
                            frame->pts = frame->best_effort_timestamp;
                        else if (frame->pts == AV_NOPTS_VALUE)
                            { av_frame_unref(frame); continue; }

                        bool shouldProcess = curReverseVideoPackets.Count - curReversePacketPos < Config.Decoder.MaxVideoFrames - Config.Decoder.MaxVideoFramesPrev; // TBR: Back Cache* (probably should add this somewhere else too

                        if (shouldProcess)
                        {
                            av_packet_free(&packet);
                            curReverseVideoPackets[curReversePacketPos - 1] = 0;
                            var mFrame = FillAVFrame();
                            if (mFrame != null)
                                curReverseVideoFrames.Add(mFrame);
                            else
                            {
                                allowedErrors--;
                                if (allowedErrors == 0) { Log.Error("Too many errors!"); Status = Status.Stopping; break; }
                            }
                        }
                        else
                            av_frame_unref(frame);
                    }

                    if (curReversePacketPos == curReverseVideoPackets.Count)
                    {
                        curReverseVideoPackets.RemoveRange(Math.Max(0, Config.Decoder.MaxVideoFramesPrev + curReverseVideoPackets.Count - Config.Decoder.MaxVideoFrames), Math.Min(curReverseVideoPackets.Count, Config.Decoder.MaxVideoFrames - Config.Decoder.MaxVideoFramesPrev) );
                        avcodec_flush_buffers(codecCtx);
                        curReversePacketPos = 0;

                        for (int i = curReverseVideoFrames.Count - 1; i >= 0; i--)
                            Frames.Enqueue(curReverseVideoFrames[i]);

                        curReverseVideoFrames.Clear();

                        break; // force recheck for max queues etc...
                    }

                } // Lock CodecCtx

            } // while curReverseVideoPackets.Count > 0

        } while (Status == Status.Running);

        if (Status != Status.Pausing && Status != Status.Paused)
            curReversePacketPos = 0;
    }

    public void RefreshMaxVideoFrames() // TODO: Transfer (all from Player?*) to renderer remove locks/check
    {
        lock (lockActions)
        {
            if (VideoStream == null)
                return;

            bool wasRunning = IsRunning;
            Renderer.ffFramesInfo.CodecId = AVCodecID.None; // TBR: force re-allocation
            Open(Stream);
            if (wasRunning)
                Start();
        }
    }

    protected override void OnSpeedChanged(double value)
    {
        if (VideoStream == null) return;
        speed = value;
        skipSpeedFrames = speed * VideoStream.FPS / (Config.Video.MaxOutputFps + 1); // Give 1 fps breath as some streams can be 60.x fps instead - cp->framerate vs av_guess_frame_rate- which one is right?)
    }

    /// <summary>
    /// Prevents to get the first frame after seek/flush
    /// </summary>
    public void ResetSpeedFrame()
        => curSpeedFrame = 0;

    /// <summary>
    /// Gets the frame number of a VideoFrame timestamp
    /// </summary>
    /// <param name="timestamp"></param>
    /// <returns></returns>
    public int GetFrameNumber(long timestamp)
        => Math.Max(0, (int)((timestamp + 2_0000 - VideoStream.StartTime + demuxer.StartTime) / VideoStream.FrameDuration));

    /// <summary>
    /// Gets the frame number of an AVFrame timestamp
    /// </summary>
    /// <param name="timestamp"></param>
    /// <returns></returns>
    public int GetFrameNumber2(long timestamp)
        => Math.Max(0, (int)((timestamp + 2_0000 - VideoStream.StartTime) / VideoStream.FrameDuration));

    /// <summary>
    /// Gets the VideoFrame timestamp from the frame number
    /// </summary>
    /// <param name="frameNumber"></param>
    /// <returns></returns>
    public long GetFrameTimestamp(int frameNumber)
        => VideoStream.StartTime + (frameNumber * VideoStream.FrameDuration);

    /// <summary>
    /// Performs accurate seeking to the requested VideoFrame and returns it
    /// </summary>
    /// <param name="frameNumber">Zero based frame index</param>
    /// <param name="backwards">Workaround for VFR for backwards frame stepping</param>
    /// <returns>The requested VideoFrame or null on failure</returns>
    /// <param name="cache">Q-0427：传入滑动窗口缓存时，解码路径上的帧会一并存入缓存而不再丢弃。</param>
    /// <param name="cacheStep">Q-0464：本次后退的帧档（1 / 5 / 10）。缓存按该步长【抽稀】：
    /// 只存"距目标 step 的整数倍"的帧，窗口跨度 K×step 帧。
    /// 此前跨度恒为 K 帧且与步长无关，于是 5F/10F 每步跨 5/10 帧、K 个名额只够 K/5、K/10 步
    /// 就耗尽 —— 击穿频率是 1F 的 5 倍 / 10 倍。抽稀后三个档位都是 K 步一填，显存不变。</param>
    /// <param name="keepExistingCache">Q-0537：true = 本次 GetFrame 不清空滑动窗口缓存（后台预填充用，见 Flush 的同名参数）。</param>
    public VideoFrame GetFrame(int frameNumber, bool backwards = false, Dictionary<long, VideoFrame> cache = null, int cacheStep = 1, bool keepExistingCache = false)
    {
        frameNumber = Math.Max(0, frameNumber);

        // Q-0427：seek 落点就是目标帧（Backward 会落到 ≤ target 的 keyframe），
        // 解码路径 [keyframe..target] 上的帧全部入缓存 —— 一次 seek 顺带填满窗口（≈GOP 帧）。
        // 【曾尝试】把落点前移到 target-K 以填满更大的窗口，已回退：缓存上限是 K，
        // 多解的 (路径-K) 帧会被淘汰浪费，1F 反而从 15ms/步 退化到 20ms/步。1F 是最高频操作，
        // 优先保它；5F/10F 靠 GOP 路径的填充也能做到 ~87ms / ~150ms（仍远快于纯 seek 的 300ms）。
        // Q-0464：上面"5F/10F 够用"的前提已改 —— 填充改为按帧档抽稀（窗口 K×step 帧、
        // 只存 step 整数倍的帧），5F/10F 的名额利用率从 K/5、K/10 回到 K 步，显存不变。
        long requiredTimestamp = GetFrameTimestamp(frameNumber);
        long curSeekMcs = requiredTimestamp / 10;
        int curFrameNumber;
        int ret;

        do
        {
            demuxer.Pause();
            Pause();
            demuxer.Interrupter.SeekRequest();
            // BLOT MODIFICATION START: serialise av_seek_frame with the demuxer thread. GetFrame is the
            // frame-step path (ShowFramePrev/ShowFrame) but it did NOT take demuxer.lockFmtCtx, so its
            // av_seek_frame raced the demuxer thread's av_read_frame on the SAME AVFormatContext -> native
            // heap corruption / AccessViolationException in av_read_frame (fail-fast, uncatchable by #4/#5).
            // Mirror DecoderContext.GetVideoFrame which already locks lockFmtCtx. Reentrant (Monitor).
            // See FLYLEAF_MODIFICATIONS.md (mod #6)
            lock (demuxer.lockFmtCtx)
            {
                ret = av_seek_frame(demuxer.FormatContext, -1, curSeekMcs - curFixSeekDelta, SeekFlags.Frame | SeekFlags.Backward);

                if (ret < 0)
                    ret = av_seek_frame(demuxer.FormatContext, -1, Math.Max((curSeekMcs - (long)TimeSpan.FromSeconds(1).TotalMicroseconds) - curFixSeekDelta, demuxer.StartTime / 10), SeekFlags.Frame);

                // BLOT MODIFICATION START (#6b): DisposePackets (clears the demuxer's AVPacket queues)
                // used to run OUTSIDE lockFmtCtx, racing the demuxer thread's enqueue (lockFmtCtx) and the
                // decoder thread's RecvFrame/Dequeue (lockCodecCtx) on the same queues -> concurrent
                // av_packet free vs use -> native heap corruption (ExecutionEngineException later).
                // Take both locks; order lockFmtCtx -> lockCodecCtx matches DecoderContext.GetVideoFrame.
                // Lock order note: DecoderContext.Seek takes codec->fmt, but it runs on the same UI thread
                // under lockActions as GetFrame (mutually exclusive), so no AB-BA here.
                // See FLYLEAF_MODIFICATIONS.md (mod #6b)
                lock (lockCodecCtx)
                    demuxer.DisposePackets();
                // BLOT MODIFICATION END (#6b)
            } // BLOT MODIFICATION END (mod #6)

            if (demuxer.Status == Status.Ended)
                demuxer.Status = Status.Stopped;

            if (ret < 0)
                return null;

            Flush(!keepExistingCache);
            checkExtraFrames = false;

            if (DecodeFrameNext() != 0)
                return null;

            curFrameNumber = GetFrameNumber2((long)(frame->pts * VideoStream.Timebase));

            // Q-0431：curFixSeekDelta 自适应收敛。
            // 原实现只在"seek 过头"时累加、永远不回落 —— 一旦被推高（all-intra 流用
            // SeekFlags.Frame seek 会反复判为"过头"，实测被累积到 2.1 秒），此后【每一次】
            // seek 都被提前 2 秒：解码路径从 ≤1 个 GOP 膨胀到数百帧，既慢（400ms/步）又让
            // 滑动窗口缓存只能收到路径头部、下一步必然 miss。
            // 修正：落点比目标早得离谱（超过 MaxLeadFrames）时，按超出量回收补偿。
            if (curFixSeekDelta > 0)
            {
                int leadFrames = frameNumber - curFrameNumber;
                if (leadFrames > MaxLeadFrames)
                {
                    // FrameDuration 单位 ticks(100ns)；curSeekMcs/curFixSeekDelta 单位微秒 -> /10
                    long excessMcs = (long)(leadFrames - MaxLeadFrames) * VideoStream.FrameDuration / 10;
                    curFixSeekDelta = Math.Max(0, curFixSeekDelta - excessMcs);
                }
            }

            if (curFrameNumber > frameNumber)
            {
                curFixSeekDelta += FIX_SEEK_DELTA_MCS;
                continue;
            }

            int cacheFilled = 0;   // Q-0427b：本次已入缓存的帧数（上限 stepCacheK）
            int pathFrames  = 0;   // Q-0464：本次解码路径的总帧数（供 all-intra 自检，见下）

            // Q-0431：只收 [target - K帧, target] 区间内的帧。此前从路径头部开始收，一旦
            // 路径被拉长（seek 落点被 curFixSeekDelta 提前数秒），收进来的全是用不到的旧帧，
            // 下一步必然 miss —— 缓存形同虚设。
            // Q-0464：跨度由 K 帧改为 K×step 帧（配合下面的抽稀，名额仍是 K 个）。
            int cacheStepFrames = Math.Max(1, cacheStep);
            // 空间换算：requiredTimestamp 是【原始 pts】空间（VideoStream.StartTime + n*FD），而
            // mFrame.Timestamp（缓存键）= pts - demuxer.StartTime（播放器时间轴）。若不减 D，窗口
            // 起点比缓存键整体高出 D 个 tick，有效窗口 = K - D/帧时长 帧 —— D 较大时（如 TS 音频流
            // 起点比视频早 12.8s，D=1534 帧）窗口变负，一帧都进不了缓存，每步后退都全量重解
            // 一个 GOP（实测 3 路 4K120/GOP=120 素材：每步 MISS empty + ~300ms/路）。
            long cacheWindowStart = requiredTimestamp - demuxer.StartTime
                                  - (long)Math.Max(0, stepCacheK) * cacheStepFrames * VideoStream.FrameDuration;
            do
            {
                bool hit = curFrameNumber >= frameNumber ||
                    (backwards && curFrameNumber + 2 >= frameNumber && GetFrameNumber2((long)(frame->pts * VideoStream.Timebase) + VideoStream.FrameDuration + (VideoStream.FrameDuration / 2)) - curFrameNumber > 1);

                // Q-0427b：仅在「窗口还没填满」时才为路径帧创建 texture；填满或缓存关闭(K=0)时
                // 一律走原路径（av_frame_unref 丢弃）。否则一旦 seek 落点异常（路径成百上千帧），
                // 会为每一帧都建 texture+SRV —— 显存暴涨且耗时失控（4K120 大流上表现为卡死）。
                bool wantCache = cache != null && cacheFilled < stepCacheK;

                if (hit || wantCache)
                {
                    // At least return a previous frame in case of Tb inaccuracy and don't stuck at the same frame
                    var mFrame = FillAVFrame();
                    if (mFrame != null)
                    {
                        if (hit)
                        {
                            // Q-0464：自检改用【路径总帧数】而不是入缓存帧数 ——
                            // step>1 时会抽稀，短路径（如 3 帧）在 step=5 下一帧都存不进缓存，
                            // 传 cacheFilled 会算出 avg=0 而把正常流误判成 all-intra 关掉缓存。
                            SampleStepCachePath(cache, pathFrames); // Q-0427b：GOP=1（all-intra）自动检出
                            return mFrame;
                        }

                        pathFrames++;

                        // Q-0427：还没到目标 → 这是"路径帧"（原实现在这里 av_frame_unref 直接丢弃，
                        // 一次 seek 解出的十几帧只留 1 帧，浪费 95%）。解码出来存进滑动窗口缓存，
                        // 后续逐帧后退可直接取用，不必再 seek。
                        // Q-0431：只把离目标最近的 K 帧收进缓存，更旧的（窗口起点之前的）
                        // 解出来后直接释放 —— 它们对"下一步后退"毫无用处，还会占满缓存名额。
                        // Q-0464：再按帧档抽稀 —— 只存"距目标 step 的整数倍"的帧。
                        //   不抽稀时 5F/10F 每步跨 5/10 帧，而窗口只有 K 帧跨度，
                        //   K 个名额只够 K/5、K/10 步（击穿频率是 1F 的 5 倍 / 10 倍）；
                        //   抽稀后窗口拉到 K×step 帧，K 个名额恰好覆盖 K 步。
                        long offsetFrames = frameNumber - curFrameNumber;   // 距目标还有几帧
                        bool aligned = offsetFrames > 0 && offsetFrames % cacheStepFrames == 0;

                        if (mFrame.Timestamp >= cacheWindowStart && aligned)
                        {
                            CacheStepFrame(cache, mFrame.Timestamp, mFrame);
                            cacheFilled++;
                        }
                        else
                            mFrame.Dispose();
                        // 注意：FillAVFrame 成功后 frame 已被换成新的空 AVFrame，故下面不能再 unref。
                    }
                    else if (!hit)
                        av_frame_unref(frame); // FillAVFrame 失败：frame 未被消耗，仍需释放
                }
                else
                    av_frame_unref(frame);

                if (DecodeFrameNext() != 0)
                    break;

                curFrameNumber = GetFrameNumber2((long)(frame->pts * VideoStream.Timebase));

            } while (true);

            return null;
        } while (true);
    }

    /// <summary>
    /// Q-0488：把一帧直接放入滑动窗口缓存（供宿主的后台预填充使用）。
    /// 与 <see cref="CacheStepFrame"/> 同规则：超限淘汰时间戳最小（离当前最远）的一帧。
    /// </summary>
    /// <summary>
    /// Q-0488：把滑动窗口缓存**整个摘出来**（清空字典但不 Dispose 帧，所有权交调用方）。
    /// 用途：GetFrame 开头会 Flush() → DisposeStepCache() 清空窗口，若在缓存还有未消费的帧时
    /// 提前填充，那批帧会被一起清掉。故提前填充前先摘出、填充后再回填（见 RestoreStepCacheFrames）。
    /// </summary>
    public List<VideoFrame> TakeStepCacheFrames()
    {
        var frames = new List<VideoFrame>(StepCache.Count);
        foreach (var f in StepCache.Values) frames.Add(f);
        StepCache.Clear();
        return frames;
    }

    /// <summary>
    /// Q-0488：把 TakeStepCacheFrames 摘出的帧回填进滑动窗口（按 <see cref="CacheStepFrame"/> 规则：
    /// 同键替换、超限淘汰时间戳最小的一帧）。缓存已关闭时直接释放，避免泄漏。
    /// 注意：回填的旧帧时间戳比新填的帧更大（更靠近当前位置），超限时会优先淘汰**新填的最早帧** ——
    /// 正是我们想要的：旧帧只剩几步寿命，不能丢；新帧损失几个最早的不影响后续连续命中。
    /// </summary>
    public void RestoreStepCacheFrames(IEnumerable<VideoFrame> frames)
    {
        if (frames == null) return;

        foreach (var f in frames)
        {
            if (f == null) continue;
            if (stepCacheK <= 0) { f.Dispose(); continue; }
            CacheStepFrame(StepCache, f.Timestamp, f);
        }
    }

    /// <summary>
    /// Q-0488：滑动窗口缓存里**最早**（时间戳最小）那一帧的时间戳。
    /// 预填充以它为基准继续往前填，才不会与"还没消费的帧"抢额度；缓存为空时返回 AV_NOPTS_VALUE。
    /// </summary>
    public long StepCacheOldestTs
    {
        get
        {
            lock (stepCacheLock)
            {
                if (StepCache.Count == 0) return AV_NOPTS_VALUE;
                var oldest = long.MaxValue;
                foreach (var key in StepCache.Keys)
                    if (key < oldest) oldest = key;
                return oldest;
            }
        }
    }
    public void PushStepCacheFrame(VideoFrame frame)
    {
        if (frame == null) return;
        if (stepCacheK <= 0) { frame.Dispose(); return; }
        CacheStepFrame(StepCache, frame.Timestamp, frame);
    }

    /// <summary>
    /// Q-0427：把一帧放入滑动窗口缓存；超出窗口（stepCacheK）时丢弃【帧号最小 = 离当前最远】的一帧并释放
    /// （VideoFrame.Dispose 幂等，会释放 texture / AVFrame，HW 下即归还 decoder surface）。
    /// 只缓存"未返回给调用者"的路径帧 —— 返回给调用者的帧会交给 VideoCache 渲染，生命周期由它管理，
    /// 若同时留在缓存里被 Dispose 会导致正在显示的帧被提前释放。
    /// </summary>
    void CacheStepFrame(Dictionary<long, VideoFrame> cache, long timestamp, VideoFrame mFrame)
    {
        if (cache == null || stepCacheK <= 0) { mFrame.Dispose(); return; }

        // 同时间戳重复解码（Tb 抖动会让同一帧被解两次）：先释放旧帧，否则其 texture / HW surface 泄漏
        if (cache.TryGetValue(timestamp, out var old))
        {
            old.Dispose();
            cache.Remove(timestamp);
        }

        cache[timestamp] = mFrame;

        while (cache.Count > stepCacheK)
        {
            long oldest = long.MaxValue;   // 时间戳最小 = 离当前最远（后退方向）
            foreach (var key in cache.Keys)
                if (key < oldest) oldest = key;

            cache[oldest].Dispose();
            cache.Remove(oldest);
        }
    }

    /// <summary>
    /// Q-0427b：滑动窗口收益自检。累计每次填充的"路径长度"（本次顺带缓存了几帧），
    /// 采样满 StepCachePathSampleCount 次后求平均：
    /// ≤1 视为 all-intra（GOP=1）—— 每帧都是关键帧，seek 一次直达，缓存零收益却白占
    /// HW surface（4K 下 K=20 ≈ +250MB；4K120/72GB 大流上曾压垮解码导致画面不动），
    /// 故自动关闭缓存并释放已缓存的帧（归还 surface）。
    /// 采样窗口每轮重置，编码参数变化（如切到 GOP 大的素材）后能重新判定。
    /// </summary>
    void SampleStepCachePath(Dictionary<long, VideoFrame> cache, int filled)
    {
        if (cache == null || stepCacheK <= 0) return;

        stepCachePathSamples++;
        stepCachePathTotal += filled;
        if (stepCachePathSamples < StepCachePathSampleCount) return;

        double avg = (double)stepCachePathTotal / stepCachePathSamples;
        stepCachePathSamples = 0;
        stepCachePathTotal = 0;

        if (avg <= 1.0)
        {
            if (CanDebug)
                Log.Debug($"[StepCache] all-intra detected (avg path {avg:F2} <= 1) -> disable sliding cache, release {StepCache.Count} frames");

            int released = StepCache.Count;
            stepCacheK = 0;
            DisposeStepCache(); // 注意：返回的那一帧不在此缓存内（生命周期归 VideoCache），安全

            // Q-0432b：关闭事件写入诊断串（Flyleaf 内部 Log 不落盘，宿主靠 StepCacheDiag 打印）。
            // 会覆盖本次的 MISS —— 没关系，事件只发生一次且比 miss 更重要；之后每步打 off。
            StepCacheDiag = $"all-intra detected (avg path {avg:F2}) -> cache disabled, released {released} frames";
        }
    }

    /// <summary>
    /// Q-0427：逐帧后退/前进时先查滑动窗口缓存。命中即从缓存移除并返回 true
    /// （移除后该帧生命周期转交调用方 / VideoCache，避免被缓存 later Dispose）。
    /// </summary>
    /// <summary> Q-0431 临时诊断：最近一次缓存查询的结果（供宿主打印，Flyleaf 自己的 Log 不落盘）。 </summary>
    internal string StepCacheDiag = "";

    /// <summary>
    /// Q-0463：上一次【从缓存取出的】帧时间戳，用于识别"假命中"（连续两次取同一帧 = 没推进）。
    /// 只在缓存命中时更新；Flush / DisposeStepCache 时复位（跳转后位置基准已变）。
    /// </summary>
    long stepCacheLastServedTs = AV_NOPTS_VALUE;

    /// <summary>
    /// Q-0431：按【Player 时间轴】查询滑动窗口缓存。目标时间 = curTime - step × 帧时长；
    /// 容差 ±半帧（pts 量化会让目标时间与缓存里的 Timestamp 差一点，精确相等匹配会落空）。
    /// </summary>
    public bool TryGetCachedStepFrame(long curTime, int step, out VideoFrame frame)
    {
        long target = curTime - (long)step * VideoStream.FrameDuration;
        long tol = VideoStream.FrameDuration / 2 + 1;

        // Q-0432b：缓存已被 all-intra 自检关闭（GOP=1：每帧都是关键帧，seek 一次直达，
        // 缓存零收益）—— 明确打 off，避免被误读成"缓存失效（MISS）"。
        if (stepCacheK <= 0)
        {
            StepCacheDiag = $"off ts={target} K=0";
            frame = null;
            return false;
        }

        // Q-0537：整个查询/取出过程与后台预填充的合并互斥（字典并发读写会损坏结构）。
        lock (stepCacheLock)
        {
        if (stepCacheK > 0 && StepCache.Count > 0)
        {
            long best = 0, bestDiff = long.MaxValue;
            foreach (var key in StepCache.Keys)
            {
                long diff = Math.Abs(key - target);
                if (diff < bestDiff) { bestDiff = diff; best = key; }
            }

            if (bestDiff <= tol)
            {
                // Q-0463：连续两次取到【同一帧】= 位置没有推进 —— 这是"假命中"，必须判为未命中，
                // 让上层走真实 seek 并在诊断串里标 STALE。
                // 实测形态：解码器 Too many errors 停摆后 CurTime 冻结，每步 target 都相同，
                // 而缓存又被同一次填充反复塞进同一帧，于是日志一直是
                // "hit ts=… got=… d=112 left=0"（连续 30 次同一个 ts），画面定格却看不出已卡死。
                // 判定为 STALE 后：不再静默成功，诊断串与后续 MISS 会如实反映问题。
                if (best == stepCacheLastServedTs)
                {
                    StepCacheDiag = $"STALE ts={target} same={best} left={StepCache.Count} K={stepCacheK} s={step} (no progress -> miss)";
                    frame = null;
                    return false;
                }

                frame = StepCache[best];
                StepCache.Remove(best);
                stepCacheLastServedTs = best;
                StepCacheDiag = $"hit ts={target} got={best} d={bestDiff} left={StepCache.Count} K={stepCacheK} s={step}";
                return true;
            }

            StepCacheDiag = $"MISS ts={target} nearest={best} d={bestDiff} n={StepCache.Count} K={stepCacheK} s={step}";
        }
        else
            StepCacheDiag = $"MISS ts={target} empty K={stepCacheK}";
        }

        frame = null;
        return false;
    }

    /// <summary>
    /// Q-0427：释放滑动窗口缓存。seek / Flush / Dispose 时必须调用 —— 缓存的帧持有 HW decoder
    /// surface 与显存，不清会持续占用；且 seek 之后旧缓存的帧号已无意义。
    /// </summary>
    internal void DisposeStepCache()
    {
        lock (stepCacheLock)
        {
        foreach (var f in StepCache.Values)
            f.Dispose();
        StepCache.Clear();
        // Q-0463：缓存作废后"上一次取出的帧"也失去意义（位置基准已变），必须复位，
        // 否则跳转后恰好解到同一时间戳会被误判成 STALE。
        stepCacheLastServedTs = AV_NOPTS_VALUE;
        }
    }

    /// <summary>
    /// Gets next VideoFrame (Decoder/Demuxer must not be running)
    /// </summary>
    /// <returns>The next VideoFrame</returns>
    public VideoFrame GetFrameNext()
    {
        checkExtraFrames = true;

        if (DecodeFrameNext() == 0)
        {
            var mFrame = FillAVFrame();
            if (mFrame != null)
                return mFrame;
        }

        return null;
    }

    /// <summary>
    /// Pushes the decoder to the next available VideoFrame (Decoder/Demuxer must not be running)
    /// </summary>
    /// <returns></returns>
    public int DecodeFrameNext()
    {
        int ret;
        int allowedErrors = Config.Decoder.MaxErrors;
        // BLOT MODIFICATION START (#8): GetNextVideoPacket() now returns the packet ownership to the caller
        // (FFmpeg-allocated; caller must av_packet_free) instead of leaving it queued inside the demuxer for
        // the next GetNextPacket() call to unref — the old model double-freed on rapid seek/frame-step and
        // leaked when ret != 0 paths returned early. See FLYLEAF_MODIFICATIONS.md (mod #8)
        AVPacket* pkt = null;

        if (checkExtraFrames)
        {
            if (Status == Status.Ended)
                return AVERROR_EOF;

            if (DecodeFrameNextInternal() == 0)
                return 0;

            if (demuxer.Status == Status.Ended && vPackets.IsEmpty && Frames.IsEmpty)
            {
                Stop(); // NOTE: Could be paused and will cause dead lock with Status ended
                Status = Status.Ended;
                return AVERROR_EOF;
            }

            checkExtraFrames = false;
        }

        while (true)
        {
            ret = demuxer.GetNextVideoPacket(out pkt);
            if (ret != 0)
            {
                if (demuxer.Status != Status.Ended || pkt == null)
                    return ret;

                // BLOT MODIFICATION START (#7): the drained packet is now caller-owned (pkt from
                // GetNextVideoPacket(out pkt)); send it inside lockCodecCtx (it races the decoder
                // thread's avcodec_receive_frame which holds the same codec) and free it after.
                // See FLYLEAF_MODIFICATIONS.md (mod #7)
                lock (lockCodecCtx)
                {
                    ret = avcodec_send_packet(codecCtx, pkt);
                    av_packet_free(&pkt);
                }
                // BLOT MODIFICATION END (#7)

                if (ret != 0)
                    return AVERROR_EOF;

                checkExtraFrames = true;
                return DecodeFrameNext();
            }

            // BLOT MODIFICATION START (#7): serialise avcodec_send_packet with DecodeFrameNextInternal's
            // avcodec_receive_frame — both touch codecCtx concurrently (decoder thread decode vs UI-thread
            // frame-step drain). The packet is caller-owned now (mod #8): send it, then av_packet_free it.
            // See FLYLEAF_MODIFICATIONS.md (mod #7)
            lock (lockCodecCtx)
            {
                if (keyPacketRequired)
                {
                    if (!pkt->flags.HasFlag(PktFlags.Key) && pkt->pts != startPts)
                    {
                        if (CanDebug) Log.Debug("Ignoring non-key packet");
                        av_packet_free(&pkt);
                        continue;
                    }

                    keyFrameRequired  = checkKeyFrame && pkt->pts != startPts;
                    keyPacketRequired = false;
                }

                ret = avcodec_send_packet(codecCtx, pkt);

                if (swFallback) // Should use 'global' packet to reset it in get_format (same packet should use also from DecoderContext)
                {
                    SWFallback();
                    ret = avcodec_send_packet(codecCtx, pkt);
                }

                av_packet_free(&pkt);
            }
            // BLOT MODIFICATION END (#7)

            if (ret != 0 && ret != AVERROR(EAGAIN))
            {
                // Q-0486：逐帧填充走的是 DecodeFrameNext（解码线程走的是 RecvAVFrame，那里已记
                // NoteEnomem）。此处此前不识别 ENOMEM —— HW surface 池耗尽时只刷 Warn 日志，
                // 宿主侧 EnomemCount 恒为 0，于是"显存被 K 撑爆"在宿主日志里毫无痕迹，
                // 现场只表现为"逐帧突然每步数百毫秒"而无从归因（K=119 / 3 路 4K 实测 2.2 万次 -12）。
                if (ret == AVERROR_ENOMEM) NoteEnomem();

                if (CanWarn) Log.Warn($"{FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");

                if (allowedErrors-- < 1)
                    { Log.Error("Too many errors!"); return ret; }

                continue;
            }

            if (DecodeFrameNextInternal() == 0)
            {
                checkExtraFrames = true;
                return 0;
            }
        }

    }
    private int DecodeFrameNextInternal()
    {
        // BLOT MODIFICATION START (#7): wrap the whole body in lockCodecCtx so the UI-thread frame-step
        // path and the decoder thread's recv/decode path are mutually exclusive on codecCtx (receive_frame
        // and send_packet on the same codec must not interleave; Monitor is reentrant so decoder paths that
        // already run under lockCodecCtx can call back in). See FLYLEAF_MODIFICATIONS.md (mod #7)
        lock (lockCodecCtx)
        {
            int ret = avcodec_receive_frame(codecCtx, frame);
            if (ret != 0) { av_frame_unref(frame); return ret; }

            if (keyFrameRequired)
            {
                if (!frame->flags.HasFlag(FrameFlags.Key)) { av_frame_unref(frame); DecodeFrameNextInternal(); }
                keyFrameRequired = false;
            }

            if (frame->best_effort_timestamp != AV_NOPTS_VALUE)
                frame->pts = frame->best_effort_timestamp;

            else if (frame->pts == AV_NOPTS_VALUE)
            {
                if (!VideoStream.FixTimestamps)
                {
                    av_frame_unref(frame);

                    return DecodeFrameNextInternal();
                }

                frame->pts = lastFixedPts + VideoStream.StartTimePts;
                lastFixedPts += av_rescale_q(VideoStream.FrameDuration / 10, Engine.FFmpeg.AV_TIMEBASE_Q, VideoStream.AVStream->time_base);
            }

            if (StartTime == NoTs)
                StartTime = (long)(frame->pts * VideoStream.Timebase) - demuxer.StartTime;

            if (!filledFromCodec) // Ensures we have a proper frame before filling from codec
            {
                ret = FillFromCodec(frame);
                if (ret == -1234)
                    return -1;
            }

            return 0;
        }
        // BLOT MODIFICATION END (#7)
    }

    #region Dispose
    // TODO: try to handle all from renderer* (requires reverse to embed in Frames)
    public void DisposeFrames()
    {
        Frames?.Reset();
        DisposeFramesReverse();
    }
    private void DisposeFramesReverse()
    {
        while (!curReverseVideoStack.IsEmpty)
        {
            curReverseVideoStack.TryPop(out var t2);
            for (int i = 0; i < t2.Count; i++)
            {
                if (t2[i] == 0) continue;
                AVPacket* packet = (AVPacket*)t2[i];
                av_packet_free(&packet);
            }
        }

        for (int i = 0; i < curReverseVideoPackets.Count; i++)
        {
            if (curReverseVideoPackets[i] == 0) continue;
            AVPacket* packet = (AVPacket*)curReverseVideoPackets[i];
            av_packet_free(&packet);
        }

        curReverseVideoPackets.Clear();

        for (int i = 0; i < curReverseVideoFrames.Count; i++)
            curReverseVideoFrames[i].Dispose();

        curReverseVideoFrames.Clear();
    }
    protected override void DisposeInternal()
    {   // Called by Dispose (lockActions) | TBR: lock (lockCodecCtx)?
        DisposeFrames();
        DisposeStepCache(); // Q-0427：缓存的帧持有 HW surface / 显存，Dispose 时必须释放
        StartTime       = AV_NOPTS_VALUE;
        swFallback      = false;
        curSpeedFrame   = 9999;
    }
    #endregion

    #region Recording
    internal Action<MediaType> recCompleted;
    Remuxer curRecorder;
    bool recKeyPacketRequired;
    internal bool isRecording;

    internal void StartRecording(Remuxer remuxer)
    {
        if (Disposed || isRecording) return;

        StartRecordTime     = AV_NOPTS_VALUE;
        curRecorder         = remuxer;
        recKeyPacketRequired= false;
        isRecording         = true;
    }
    internal void StopRecording() => isRecording = false;
    #endregion
}
