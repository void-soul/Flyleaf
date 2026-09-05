# Flyleaf 修改记录 (Flyleaf Modifications)

本文档记录了 BlotEyes 项目对 Flyleaf 库的所有修改，以便在升级 Flyleaf 版本时能够快速重新应用这些修改。

## 前置条件

将 FlyleafLib 库 link 到项目目录

```
mklink /D "E:\pro\gld\blot-eyes\FlyleafLib.Controls.WPF" "E:\pro\other-sdk\Flyleaf\FlyleafLib.Controls.WPF"
mklink /D "E:\pro\gld\blot-eyes\FlyleafLib" "E:\pro\other-sdk\Flyleaf\FlyleafLib"
```

## 版本信息

- **当前 Flyleaf 版本**: 3.10.4-blot（基于 v3.10.4 commit `74efdd7` + fix #688 commit `c2fbb55` + BLOT 自定义修改）
- **BLOT 定制修改日期**: 2026-04-23 ~ 2026-09-04（#6b/#7/#8 于 2026-09-03 新增；#9 于 2026-09-04 新增）

## 修改总览

| # | 文件 | 改动 | 类型 | 说明 |
|---|------|------|------|------|
| 1 | Demuxer.cs | 动态 duration 更新 | BLOT 自定义 | 无冲突 |
| 2 | Demuxer.cs | EOF 重试机制 | BLOT 自定义 | 无冲突 |
| 2b | Demuxer.cs | EOF 重试等待移出 `lockFmtCtx` | BLOT 自定义 | 无冲突 |
| 3 | Player.Screamers.VASD.cs | live 流 buffer 重试 | BLOT 自定义 | 无冲突 |
| 4 | RunThreadBase.cs | 线程 runloop 异常兜底 | BLOT 自定义 | 无冲突 |
| 5 | CustomIOContext.cs / Interrupter.cs | native 边界托管异常兜底 | BLOT 自定义 | 无冲突 |
| 6 | Demuxer.cs / VideoDecoder.cs | 逐帧/取帧 native 操作加 `lockFmtCtx` | BLOT 自定义 | 无冲突（根治快进退/逐帧 av_read_frame 闪退） |
| 6b | VideoDecoder.cs | `GetFrame` 的 `DisposePackets` 纳入 `lockFmtCtx`+`lockCodecCtx` | BLOT 自定义 | 无冲突（消除锁外清包与 demuxer/decoder 线程并发释放） |
| 7 | VideoDecoder.cs | 逐帧路径 codec 操作加 `lockCodecCtx` | BLOT 自定义 | 无冲突（根治逐帧与解码线程 codecCtx 并发 → ExecutionEngineException） |
| 8 | Demuxer.cs / VideoDecoder.cs | 逐帧取包改为 `out AVPacket*` 所有权转移，不再共享 `packet` 字段 | BLOT 自定义 | 改方法签名（**根治 AB 逐帧/下一帧必现 ExecutionEngineException**） |
| 9 | SwapChain.cs / Renderer.Present.cs / Renderer.VP.D3.cs | SwapChain 释放与渲染循环互斥 + 锁内复核 + `VideoProcessorBlt` null 守卫 | BLOT 自定义 | 无冲突（**根治快速切组回看 VideoProcessorBlt 崩溃**，Q-0319） |

> ⚠️ **三处修改必须同时存在**，互为依赖：
> - 修改 #1 把 `IsLive` 置 `true` → 修改 #3 才会进 live-retry 分支。
> - 修改 #2 让 demuxer EOF 时等数据 → 等待期间 decoder 队列暂时为空，需要修改 #3 兜住 screamer。
>
> **只保留 #1 是最坑的状态**：进度条会涨一点点，看似工作，但 demuxer 一追到尾巴就 `Status.Ended`，screamer 立即 `[V] Buffer Empty → break`，播放停掉。Flyleaf 重新同步后**务必跑下方的"自检"命令**确认 3 个修改都还在。

## 背景：录制端 GOP=1 设计

所有 BLOT 修改的前提是录制端强制 GOP=1：

- 相机设置 GOP=1，录制流强制转换为 GOP=1
- Pipeline 检测输入 GOP，若与目标不同则 NVENC 转码，否则直接 copy
- Muxer 配置：`pcr_period=20` + `resend_headers` + `omit_video_pes_length=1`
- 录制格式：mpegts (`.ts`)

---

## BLOT 自定义修改（无冲突，升级时直接重新应用）

### 1. Demuxer.cs — 动态 duration 更新

**文件**: `FlyleafLib/MediaFramework/MediaDemuxer/Demuxer.cs`
**方法**: `RunInternal()`
**修改日期**: 2026-04-23
**解决问题**: 播放正在录制的 TS 文件时，播放器进度条不自动更新

**原理**: Flyleaf 打开文件时读取一次 duration，之后不再更新。录制进程持续写入新数据，但播放器不知道文件变长了。

**修改内容**:

在 `RunInternal()` 方法中，`av_read_frame()` 成功后、`if (CanTrace)` 之前插入：

```csharp
// BLOT MODIFICATION START: Dynamic duration update for growing MPEGTS files
if (Name == "mpegts" && packet->pts != NoTs)
{
    var stream = AVStreamToStream[packet->stream_index];
    long currentPtsTicks = (long)(packet->pts * stream.Timebase);
    if (currentPtsTicks > Duration)
    {
        Duration = currentPtsTicks;
        fmtCtx->duration = Duration / 10; // FFmpeg duration is in AV_TIME_BASE (1,000,000)
        IsLive = true;                    // Force player to treat it as a growing stream
        HLSDurationChanged?.Invoke(Duration);
    }
}
// BLOT MODIFICATION END
```

**位置标识**: 在 `if (IsHLSLive) UpdateHLSTime();` 之后，`if (CanTrace) { ... }` 之前。

**关键设计**: `IsLive = true` 是连接 demuxer 和 screamer 的桥梁——demuxer 设置它，screamer 的 buffer 重试（修改 #4）依赖它。

### 2. Demuxer.cs — EOF 重试机制

**文件**: `FlyleafLib/MediaFramework/MediaDemuxer/Demuxer.cs`
**方法**: `RunInternal()`
**修改日期**: 2026-06-01
**解决问题**: 播放正在录制的 TS 文件时，到达文件末尾后播放器立即停止

**原理**: demuxer 读到 EOF 时，录制进程可能还在写入新数据。立即结束播放是错误的。

**修改内容**:

在 `RunInternal()` 方法开头（`do` 循环之前）添加局部变量：

```csharp
// BLOT MODIFICATION: EOF retry counter for growing MPEGTS files
const int MAX_MPEGTS_EOF_RETRIES = 60; // 60 * 500ms = 30 seconds max wait for new data
int mpegtsEofRetries = 0;
```

在 `av_read_frame()` 返回 `AVERROR_EOF` 时：

```csharp
if (ret == AVERROR_EOF)
{
    // BLOT MODIFICATION: For growing MPEGTS files, don't end immediately
    // Wait for new data to be appended by the recording process
    // After maxEofRetries with no new data, treat as real EOF
    if (Name == "mpegts" && mpegtsEofRetries < MAX_MPEGTS_EOF_RETRIES)
    {
        mpegtsEofRetries++;
        gotAVERROR_EXIT = true;
        Thread.Sleep(500);
        continue;
    }

    Status = Status.Ended;
    break;
}
```

在成功读取包后（`ret == 0` 分支中）重置计数器：

```csharp
// BLOT MODIFICATION: Reset EOF retry counter on successful read
mpegtsEofRetries = 0;
```

**逻辑说明**:
- mpegts 格式 EOF 时不立即结束，等待 500ms 后重试
- 最多重试 60 次（30 秒），之后视为真正 EOF
- 成功读取到新包时重置计数器
- 非 mpegts 格式不受影响

### 3. Player.Screamers.VASD.cs — live 流 buffer 重试

**文件**: `FlyleafLib/MediaPlayer/Player.Screamers.VASD.cs`
**方法**: `ScreamerVASD()`
**修改日期**: 2026-06-01
**解决问题**: demuxer 等待新数据期间 decoder 队列耗尽，screamer 退出播放

**原理**: 当 demuxer 在 EOF 重试（修改 #2）时，decoder 没有新数据可解码，vFrame 会返回 null。原代码直接 break 退出 screamer，导致播放终止。

**修改内容**:

```csharp
if (vFrame == null)
{
    if (decoderHasEnded)
    {
        OnBufferingCompleted();
        break;
    }

    // BLOT MODIFICATION: For live/growing streams, retry buffering instead of stopping
    if (isLive)
    {
        Log.Warn("[V] Buffer Empty (live) - retrying");
        requiresBuffering = true;
        continue;
    }

    Log.Warn("[V] Buffer Empty");
    break;
}
```

**逻辑说明**:
- `decoderHasEnded` 为 true → 正常结束（break）
- `isLive` 为 true（growing .ts 文件）→ 重试 buffering（continue）
- 其他情况 → 正常结束（break）

**与修改 #1 的协同**: 修改 #1 中设置 `IsLive = true`，使得 screamer 能识别 growing 流并进入重试逻辑。

---

### 2b. Demuxer.cs — EOF 重试等待移出 `lockFmtCtx`（快进退/逐帧崩溃的诱因之一）

**文件**: `FlyleafLib/MediaFramework/MediaDemuxer/Demuxer.cs`
**方法**: `RunInternal()`
**修改日期**: 2026-06（遗留未记录，2026-08 补档）
**解决问题**: 修改 #2 在 EOF 重试时 `Thread.Sleep(500)` 是在 `lock (lockFmtCtx)` **内部**执行的——每轮最多睡 500ms，EOF 场景最多 60 轮（约 30s）会把格式上下文锁一直占住，让并发 `Seek()`/`av_seek_frame`/逐帧 native 操作排队积压，并在排队期间与仍在运行的 native 读操作发生竞争（`av_read_frame` 崩溃）。

**修改内容**:

1. 在 `RunInternal()` 的局部变量区（`mpegtsEofRetries` 旁）新增：

```csharp
// BLOT MODIFICATION (2b): pending EOF-retry wait (ms), performed OUTSIDE lockFmtCtx
int eofRetryWaitMs = 0;
```

2. EOF 分支里把 `Thread.Sleep(500)` 改为"登记等待 + 退出锁块"：

```csharp
if (Name == "mpegts" && mpegtsEofRetries < MAX_MPEGTS_EOF_RETRIES)
{
    mpegtsEofRetries++;
    gotAVERROR_EXIT = true;
    eofRetryWaitMs = 500;   // 记下等待量，然后 break 离开 lockFmtCtx
    break;
}
```

3. 在 `do { ... } while (Status == Status.Running)` 循环**末尾、锁外**执行等待：

```csharp
// BLOT MODIFICATION START (2b): EOF-retry wait executed OUTSIDE lockFmtCtx ...
if (eofRetryWaitMs > 0) { Thread.Sleep(eofRetryWaitMs); eofRetryWaitMs = 0; }
// BLOT MODIFICATION END
```

**逻辑说明**: 语义不变（仍是 60 次重试、成功读包即清零），只是把 sleep 从锁内移到锁外，让等待新数据期间 `lockFmtCtx` 可被 seek/逐帧使用，避免排队堆积诱发 native 竞争。

---

### 4. RunThreadBase.cs — 线程 runloop 异常兜底（防托管异常逃逸崩进程）

**文件**: `FlyleafLib/MediaFramework/RunThreadBase.cs`
**方法**: `Run()`
**修改日期**: 2026-08（补档）
**解决问题**: 每个 RunThreadBase 派生线程（Demuxer/Decoder 等）的 `RunInternal()` 若有托管异常逃逸（例如 AVIO 回调解跨 FFmpeg native 帧抛出），.NET 5+ 默认会 fail-fast 直接崩掉整个进程。

**修改内容**: 把 `RunInternal()` 调用包进 `try/catch`，异常时留痕并将状态置为 `Stopping`（走正常的线程收尾）：

```csharp
try
{
    RunInternal();
}
catch (Exception e)
{
    Log.Error($"Unhandled exception in {threadName} ({Status}): {e}");
    lock (lockStatus)
        if (Status == Status.Running || Status == Status.QueueFull || Status == Status.QueueEmpty || Status == Status.Draining)
            Status = Status.Stopping;
}
```

**注意**: 该兜底只能拦 **托管** 异常。真正的快进退/逐帧闪退是 **native `AccessViolationException`**（见修改 #6），.NET 5+ 默认不可 catch，本修改与 #5 拦不住它——根治靠 #6。

---

### 5. CustomIOContext.cs / Interrupter.cs — native 边界托管异常兜底

**文件**: `FlyleafLib/MediaFramework/MediaDemuxer/CustomIOContext.cs`、`Interrupter.cs`
**方法**: `CustomIOContext.IORead()` / `IOSeek()`、`Interrupter.ShouldInterrupt()`
**修改日期**: 2026-08-27（补档；`IOSeek` 降级语义 2026-08-27 修正）
**解决问题**: 这些方法由 FFmpeg 在 `av_read_frame`/`av_seek_frame` 内部**跨 P/Invoke 反向回调**调用。若底层 `Stream`（正在录制的 .ts 文件）在读/seek 中途被释放/关闭而抛出托管异常，异常会解跨 FFmpeg native 栈而 fail-fast 崩掉进程。

**修改内容**: 三个方法体各包一层 `try/catch`，异常时降级为 `AVERROR_EXIT`（或降级返回合法值），绝不把托管异常抛出 native 边界：

- `IORead()`: catch 后记日志，返回 `AVERROR_EXIT`（并处理 `stream == null` 的已释放场景）。
- `IOSeek()`: catch 后记日志（含 `e.GetType().Name`）；`whence == Size` 返回 `stream?.Length ?? 0`，**其余 whence 返回 `-1`**（非 `Size` 的 seek 失败必须让 FFmpeg 判定失败，而非给一个猜测/错误的偏移位置——返回错误偏移会让 FFmpeg 按错位继续读造成静默错帧，比硬错误更糟）。
- `ShouldInterrupt()`: 原逻辑拆到 `ShouldInterruptCore()`，`ShouldInterrupt()` 包 try/catch，异常时返回 `0`（不中断）。

---

### 6. Demuxer.cs / VideoDecoder.cs — 逐帧/取帧 native 操作加 `lockFmtCtx`（**根治 av_read_frame 闪退**）

**文件**: `FlyleafLib/MediaFramework/MediaDemuxer/Demuxer.cs`、`FlyleafLib/MediaFramework/MediaDecoder/VideoDecoder.cs`
**方法**: `Demuxer.GetNextPacket()`、`VideoDecoder.GetFrame(int, bool)`
**修改日期**: 2026-08-27
**解决问题**: **快进退/逐帧重叠操作时 `av_read_frame` 原生闪退（0xc0000005 / AccessViolation，进程直接终止）**。

**根因**（本次全新定位）: Flyleaf 自己的约定是：所有对共享 `AVFormatContext` 的 native 操作都要在 `lock (lockFmtCtx)` 下进行——`Demuxer.RunInternal()` 的 `av_read_frame`、`Demuxer.Seek()` 的 `av_seek_frame`、`DecoderContext.GetVideoFrame()` 的 `av_read_frame` 都遵循了。但**逐帧/取帧这条路径漏了**：

- `VideoDecoder.GetFrame()`（被 `ShowFramePrev` / `ShowFrame(int)` 调用）里直接 `av_seek_frame(demuxer.FormatContext, ...)`，**没加 `lockFmtCtx`**（VideoDecoder.cs:1040）。
- `VideoDecoder.DecodeFrameNext()` → `demuxer.GetNextPacket()` 里的 `av_read_frame(fmtCtx, packet)`（Demuxer.cs:1842），**也没加 `lockFmtCtx`**。

于是 UI 线程跑逐帧（`ShowFrameNext`/`ShowFramePrev`，经 `GetFrameNext`/`GetFrame`）的同时，demuxer 线程正在 `lockFmtCtx` 下 `av_read_frame`——**两个线程对同一个 `AVFormatContext` 并发执行 native 读写**（一个在 seek 回退、一个在读包），堆内存被破坏 → 下一次 `av_read_frame` 抛原生 AccessViolation → .NET 5+ fail-fast 终止进程。闪退栈里 `av_read_frame` 出现在 Demuxer.RunInternal 之上，正是这两处之一。

> 为什么现有 #4/#5 挡不住：AccessViolationException 是 **native** 故障，.NET 5+ 默认**不可被 `catch (Exception)` 捕获**，进程直接终止。规避它只能靠**消除并发**，不能靠 try/catch。

**修改内容**:

1. `VideoDecoder.GetFrame()` — 把两处 `av_seek_frame` 用 `lock (demuxer.lockFmtCtx)` 包住（与 `DecoderContext.GetVideoFrame` 的既有写法一致；`Monitor` 可重入，内层 `DecodeFrameNext→GetNextPacket` 再打锁不会死锁，跨线程则与 demuxer 互斥）：

```csharp
demuxer.Interrupter.SeekRequest();
// BLOT MODIFICATION START ...
lock (demuxer.lockFmtCtx)
{
    ret = av_seek_frame(demuxer.FormatContext, -1, curSeekMcs - curFixSeekDelta, SeekFlags.Frame | SeekFlags.Backward);
    if (ret < 0)
        ret = av_seek_frame(demuxer.FormatContext, -1, Math.Max((curSeekMcs - (long)TimeSpan.FromSeconds(1).TotalMicroseconds) - curFixSeekDelta, demuxer.StartTime / 10), SeekFlags.Frame);
} // BLOT MODIFICATION END (mod #6)
demuxer.DisposePackets();
```

（`int ret` 需声明在 `do` 循环外，见文件名：VideoDecoder.cs:1033 附近新增 `int ret;`。）

2. `Demuxer.GetNextPacket()` — 把 `av_read_frame(fmtCtx, packet)` 及其处理体用 `lock (lockFmtCtx)` 包住，**但 `Stop()` 必须放在锁外**（见下）：

```csharp
while (true)
{
    bool eofStop = false;
    // BLOT MODIFICATION START ...
    lock (lockFmtCtx)
    {
        Interrupter.ReadRequest();
        ret = av_read_frame(fmtCtx, packet);
        if (ret != 0)
        {
            av_packet_unref(packet);
            if ((ret == AVERROR_EXIT && fmtCtx->pb != null && fmtCtx->pb->eof_reached != 0) || ret == AVERROR_EOF)
            {
                packet = av_packet_alloc();
                packet->data = null;
                packet->size = 0;
                eofStop = true;
            }
        }
        else if (streamIndex != -1 ? packet->stream_index == streamIndex
                                   : EnabledStreams.Contains(packet->stream_index))
        {
            return 0;
        }
        else
        {
            av_packet_unref(packet);
        }
    } // BLOT MODIFICATION END (mod #6)

    if (eofStop) { Stop(); Status = Status.Ended; } // Stop() 在锁外
    if (ret != 0)
        return ret;
}
```

> **关键细节**: `Stop()`（`RunThreadBase.Stop()`）会**等待 demuxer 线程退出**。若在 `lock (lockFmtCtx)` 内调用 `Stop()`，而 demuxer 线程恰好停在 `lock (lockFmtCtx)` 入口等待该锁（快进退/逐帧重叠、demuxer 尚未真正暂停的窗口），则 `Stop()` 等 demuxer、demuxer 等锁 → **死锁卡死**。因此 EOF 分支只把 `eofStop` 置位，退出锁块后再 `Stop()`。

**死锁分析**: ① 同线程内 `lock` 可重入，`GetFrame` 的锁与内部 `GetNextPacket` 的锁嵌套无死锁；② demuxer 线程只持有 `lockFmtCtx` 而不反向等待逐帧线程序持有的锁，逐帧线程持锁时有界地做一次读/seek 后即释放，无循环等待；③ `Stop()` 一律在 `lockFmtCtx` **之外**调用，避免锁等待环；④ 与既有 `DecoderContext.GetVideoFrame` 的加锁模式一致，未引入新锁顺序。

**验证**: `dotnet build FlyleafLib/FlyleafLib.csproj` 0 error。逐帧/快进退压测复现 -> 已消除 av_read_frame 闪退。

---

### 6b. VideoDecoder.cs — `GetFrame` 的 `DisposePackets` 纳入双锁（锁外清包 use-after-free）

**文件**: `FlyleafLib/MediaFramework/MediaDecoder/VideoDecoder.cs`
**方法**: `GetFrame(int, bool)`
**修改日期**: 2026-09-03
**解决问题**: 修改 #6 把 `av_seek_frame` 锁进了 `lockFmtCtx`，但紧随其后的 `demuxer.DisposePackets()`（清空 demuxer 的 AVPacket 队列并释放其中的 native 包）仍在**锁外**执行——与 demuxer 线程的入队（`lockFmtCtx`）和解码线程的 `RecvFrame`/`vPackets.Dequeue`（`lockCodecCtx`）并发，造成 native 包一边被释放一边被使用 → 堆损坏，之后以 `ExecutionEngineException`（0x80131506，无堆栈）延迟引爆。

**修改内容**: 把 `DisposePackets()` 移入既有的 `lock (demuxer.lockFmtCtx)` 块内，并再嵌一层 `lock (lockCodecCtx)`：

```csharp
lock (demuxer.lockFmtCtx)
{
    ret = av_seek_frame(...);
    if (ret < 0)
        ret = av_seek_frame(...);

    // BLOT MODIFICATION START (#6b) ...
    lock (lockCodecCtx)
        demuxer.DisposePackets();
    // BLOT MODIFICATION END (#6b)
}
```

**死锁分析**: 锁序 `lockFmtCtx → lockCodecCtx` 与 `DecoderContext.GetVideoFrame`（497-498 行）一致。反序持有者 `DecoderContext.Seek`（codec→fmt）与 `GetFrame` 同在 UI 线程且都处于 `lockActions` 之下（互斥不并发），无 AB-BA。

---

### 7. VideoDecoder.cs — 逐帧路径 codec 操作加 `lockCodecCtx`（**根治"下一帧偶发 ExecutionEngineException"**）

**文件**: `FlyleafLib/MediaFramework/MediaDecoder/VideoDecoder.cs`
**方法**: `DecodeFrameNext()` / `DecodeFrameNextInternal()`
**修改日期**: 2026-09-03
**解决问题**: 视频解码线程（`VideoDecoder.RunInternal`）在 `lock (lockCodecCtx)` 下执行 `RecvAVFrame`/`SendAVPacket`；而 UI 线程逐帧路径 `ShowFrameNext → GetFrameNext → DecodeFrameNext` 对**同一个 `codecCtx`** 的 `avcodec_send_packet`/`avcodec_receive_frame` **完全无锁**（Flyleaf 源码文件头自述 *"Missing locks (e.g. GetFrameNext)"*）。`ShowFrameNext` 开头的 `Pause()` 只是置状态，解码线程真正停下来有时间差——**刚暂停就步进**即并发使用 codecCtx → native 堆损坏 → 偶发 `ExecutionEngineException`（进程 fail-fast，#4/#5 的托管 try/catch 拦不住）。4K120 高码率文件解码吞吐大，撞上窗口的概率更高。

**修改内容**:

1. `DecodeFrameNext()` 中所有触碰 `codecCtx` / 共享 `demuxer.packet` 的区段（drain 分支的 send+unref、keyPacketRequired 检查、主 send+`SWFallback`+unref）包进 `lock (lockCodecCtx)`。
2. `DecodeFrameNextInternal()` 整个方法体（`avcodec_receive_frame` + 共享 `frame` 处理 + `FillFromCodec`）包进 `lock (lockCodecCtx)`——`FillFromCodec → ConfigHWFrames` 本就注明"Locked by lockCodecCtx"，原逐帧路径未持锁反而是隐患。Monitor 可重入，方法内递归调用安全。

**锁序设计（关键）**: `demuxer.GetNextVideoPacket()`（内部 `lockFmtCtx`，修改 #6）刻意留在 `lockCodecCtx` **之外**——本修改中两锁**永不嵌套**，因此：
- 解码线程只持 `lockCodecCtx`（其循环内不取 `lockFmtCtx`）→ 与 UI 线程互斥无环；
- demuxer 线程只持 `lockFmtCtx` → 无环；
- `DecoderContext.GetVideoFrame` 的 `lockFmtCtx → lockCodecCtx` 嵌套与 `DecoderContext.Seek` 的反向顺序是 Flyleaf 既有（TBR 注释）状况，本修改未新增任何嵌套方向。
- `DecodeFrameNext` 的 `Stop()`（等待解码线程退出）保持在自己所持锁之外，避免"持锁等线程、线程等锁"死锁。

**验证**: 逐帧/AB 逐帧（尤其 4K120 文件、刚暂停立即步进、多机位同按）压测不再出现 `ExecutionEngineException`。

---

### 8. Demuxer.cs / VideoDecoder.cs — 逐帧取包改为 `out AVPacket*` 所有权转移（**根治 AB 逐帧/下一帧必现 ExecutionEngineException**）

**文件**: `FlyleafLib/MediaFramework/MediaDemuxer/Demuxer.cs`、`FlyleafLib/MediaFramework/MediaDecoder/VideoDecoder.cs`
**方法**: `Demuxer.GetNextVideoPacket()` / `Demuxer.GetNextPacket()` / `VideoDecoder.DecodeFrameNext()`
**修改日期**: 2026-09-03
**解决问题**: **共享 `packet` 字段跨线程竞争**。demuxer 线程 `RunInternal` 与 UI 逐帧路径共用 demuxer 的 `packet` 字段：

- demuxer 线程（seek/暂停后的重新灌缓冲期间处于 Running）：`av_read_frame(fmtCtx, packet)` → 入队 → `packet = av_packet_alloc()`，反复改写字段；
- UI 逐帧线程：`packet = VideoPackets.Dequeue()`（写字段）→ `avcodec_send_packet(codecCtx, demuxer.packet)` → `av_packet_unref(demuxer.packet)`（读/释放字段）。

两者并发 = 同一 `AVPacket*` 一边被改写/释放一边被使用 → double-unref / use-after-free → 堆损坏 → `ExecutionEngineException`（延迟引爆、无堆栈、不可 catch）。该缺陷为 **stock Flyleaf 既有问题**（文件头 TBR 自述 *"Missing locks"*，文档要求 "Demuxer must not be running" 但暂停/seek 后的灌缓冲窗口内 demuxer 恰恰在跑）。修改 #7 在"填字段"与"用字段"之间插入了 `lockCodecCtx` 等待（与灌缓冲期间同样活跃的解码线程争锁），把原本纳秒级的偶发窗口拉宽到毫秒级 → AB 60fps 逐帧**必现**闪退（Q-0393 实测：两个 120fps ALL-INTRA 文件，seek 后 demuxer 需重读 ~50MB 才能填满缓冲，期间 AB 已开始 16.7ms 步进）。

**修改内容**（方法签名变更）:

1. `GetNextVideoPacket(out AVPacket* pkt)`：优先从 `VideoPackets` 出队（`PacketQueue` 自带锁，线程安全），出队包所有权转移给调用方；队列空则走 2。
2. `GetNextPacket(int streamIndex, out AVPacket* pkt)`：在 `lockFmtCtx`（#6）内用**本地** `av_packet_alloc` 包 `av_read_frame`；匹配流 → 所有权转移；不匹配 → unref 重试；EOF → 交给调用方一个空 drain 包（`data=null,size=0`）；非 EOF 错误 → 本地 `av_packet_free`，`pkt=null`。**全程不碰共享 `packet` 字段**（该字段此后专属于 demuxer 线程）。
3. `VideoDecoder.DecodeFrameNext()`：改用本地 `pkt`；释放纪律从 `av_packet_unref` 升级为 `av_packet_free`（与解码线程 `SendAVPacket` 一致——出队包的 struct 由 av_packet_alloc 分配，必须 free，原实现每步进一帧泄漏一个 AVPacket struct）；drain 分支增加 `pkt == null` 守卫。

**升级注意**: 签名变了——若上游 Flyleaf 的 `GetNextVideoPacket`/`GetNextPacket` 被其他代码调用，需一并适配为 out 参数形式（当前 BlotEyes/Flyleaf 内唯一调用方是 `DecodeFrameNext`）。

**验证**: `dotnet build FlyleafLib` + `dotnet build BlotEyes.Player` 均 0 error；用 PTS 时钟异常文件（如 E:\0001\1st 的 120fps ALL-INTRA 对）AB 60fps 循环 + 快速连续下一帧压测不再闪退。

---

### 9. SwapChain.cs / Renderer.Present.cs / Renderer.VP.D3.cs — SwapChain 释放与渲染循环互斥（**根治快速切组回看 VideoProcessorBlt 崩溃**）

**文件**: `FlyleafLib/MediaFramework/MediaRenderer/SwapChain.cs`、`Renderer.Present.cs`、`Renderer.VP.D3.cs`
**方法**: `SwapChain.DisposeLocal()`、`Renderer.RenderPlay()` / `RenderIdle()`、`Renderer.D3Render()`
**修改日期**: 2026-09-04
**解决问题**: 录制端快速切换分组放大（回看）时进程崩溃——生产 Windows 事件日志：`ID3D11VideoContext.VideoProcessorBlt ← Renderer.D3Render ← RenderPlay ← ScreamerVASD ← PlayThread`，进程因未处理异常终止。

**根因**（多线程竞争）: BlotEyes 双实例模式 `ShowPlayer` 切换 `FlyleafHost.Player` 绑定时，`FlyleafHost.SetPlayer` 会无条件 `Dispose` 旧播放器的 SwapChain（`VPOV`/`bbRtv`/`sc` 等）。渲染侧 `RenderPlay` 只在取 `lockRenderLoops` **前**检查一次 `CanPresent`（check-then-act），而释放侧 `DisposeLocal` 走 `lockDevice`/`lockDispose`——**两把锁互不互斥** → 渲染线程 in-flight 的 `VideoProcessorBlt` 拿到已释放的输出视图抛 `SharpGenException`，直达线程顶层（旧构建无 catch 兜底）。进入回看方向 `PreviewPlayer` 常驻播放（SSP 4K120，每帧 8.3ms 一次 Blt）放大了踩中概率。

**修改内容**（5 处，代码标记 `BLOT MODIFICATION (Q-0319)`）:

1. `SwapChain.DisposeLocal()`: ① `CanPresent = false` 提前到方法最前（渲染循环尽早退出）；② GPU 资源释放（`VPOV`/`bbRtv`/`bb`/`sc`/`dc*`/`DisposeHelper`）整体纳入 `lock (Renderer.lockRenderLoops)`。锁序 `lockDevice → lockRenderLoops` 与 `ClearScreen(force)` 既有顺序一致，无新锁倒置。
2. `Renderer.RenderPlay()`: `lock (lockRenderLoops)` 内复核 `SwapChain.Disposed / CanPresent`（捕获"过检查后在等锁期间被 Dispose"的漏网渲染）。
3. `Renderer.RenderIdle()`: 同上。
4. `Renderer.lockRenderLoops` 声明 private → internal（供 `SwapChain.DisposeLocal` 共用渲染锁）。
5. `Renderer.D3Render()`: 顶部增加 `vp == null || SwapChain.VPOV == null` 守卫（纵深防御，覆盖快照渲染等其他调用路径）。

**配套 BlotEyes 侧**（非 FlyleafLib，`BlotEyes.PlayerOverlay/Controls/EnhancedPlayerControl.cs`，QA Q-0319）:
- `ShowPlayer` 顺序反转：先 `ApplyHiddenPlayerBehavior`（停被隐藏播放器）再切 `FlyleafHost` 绑定，消除退出回看方向的竞争窗口；
- `OpenFileOnFilePlayer` 同步 `Open` → `OpenAsync`（UI 线程不再同步探测正被录制的 TS 文件——切组卡死的主因，属 IO 锁定）。

**验证**: `dotnet build` 0 error；`dotnet test BlotEyes.Tests` 全部通过。快速连续切组放大/退出回看不再崩溃。

---

## 已合并的上游修改

### fix #688 — 字幕分析策略变更 (2026-05-29, commit `c2fbb55`)

**已手动应用到本仓库**，涉及 3 个文件：

- **Demuxer.cs**: 注释掉 mpegts 字幕 probesize/max_analyze_duration 增大逻辑，改为在 renderer 阶段按需处理
- **Renderer.VP.Subs.cs**: 添加 subsSize guard，在 subsSize 无效时从 decoder 的 CodecCtx 读取 w/h
- **SubtitlesDecoder.cs**: 添加注释说明应同步更新 subs renderer

**升级时注意**: 此 fix 已合并，升级 Flyleaf 时如果上游已包含此 commit，跳过即可。

#### 变更追溯

- **2026-06-25**: 与 `E:\pro\other-sdk\Flyleaf-source` 对齐后发现 `Demuxer.cs` 上 c2fbb55 的注释化改动当时**漏合**（仓库里那块 probesize/max_analyze_duration 仍为启用态），同时还原了一处被改成 `[...]` 集合表达式的微小本地化分歧。本次补合后，两个文件相对上游的 diff 应仅剩 BLOT MODIFICATION（当时是 #1-#3 共 6 行标记）。
- **2026-08-27**: 新增并补档 BLOT 修改 #2b / #4 / #5 / #6（见上），快进退/逐帧 av_read_frame 闪退由 #6 根治。

---

## 升级 Flyleaf 的步骤

### 1. 备份当前修改

```bash
cd Flyleaf
git diff > ../flyleaf-modifications-backup.patch
```

### 2. 更新 Flyleaf

```bash
git fetch origin
git checkout <new-version-tag>
```

### 3. 重新应用 BLOT 自定义修改（无冲突，直接应用）

搜索代码中的 `// BLOT MODIFICATION` 注释，或按本文档重新插入：

- [ ] **修改 #1**: Demuxer.cs `RunInternal()` — 动态 duration 更新
- [ ] **修改 #2**: Demuxer.cs `RunInternal()` — EOF 重试机制
- [ ] **修改 #2b**: Demuxer.cs `RunInternal()` — EOF 重试等待移出 `lockFmtCtx`
- [ ] **修改 #3**: Player.Screamers.VASD.cs `ScreamerVASD()` — live 流 buffer 重试
- [ ] **修改 #4**: RunThreadBase.cs `Run()` — 线程 runloop 异常兜底
- [ ] **修改 #5**: CustomIOContext.cs / Interrupter.cs — native 边界托管异常兜底
- [ ] **修改 #6**: Demuxer.cs `GetNextPacket()` + VideoDecoder.cs `GetFrame()` — 逐帧 native 加 `lockFmtCtx`（根治闪退，**必做**）
- [ ] **修改 #6b**: VideoDecoder.cs `GetFrame()` — `DisposePackets` 纳入 `lockFmtCtx`+`lockCodecCtx` 双锁
- [ ] **修改 #7**: VideoDecoder.cs `DecodeFrameNext()`/`DecodeFrameNextInternal()` — 逐帧 codec 操作加 `lockCodecCtx`（根治下一帧偶发 ExecutionEngineException，**必做**）
- [ ] **修改 #8**: Demuxer.cs `GetNextVideoPacket`/`GetNextPacket` + VideoDecoder.cs `DecodeFrameNext` — out 所有权转移、不共享 `packet` 字段（根治 AB 逐帧必现闪退，**必做**，注意签名变更）
- [ ] **修改 #9**: SwapChain.cs `DisposeLocal()` + Renderer.Present.cs `RenderPlay()`/`RenderIdle()` + Renderer.VP.D3.cs `D3Render()` — SwapChain 释放与渲染循环互斥（根治快速切组回看 VideoProcessorBlt 崩溃，**必做**）

### 3.1 自检：确认所有修改都在

应用完后跑一遍这条命令，确认所有 `// BLOT MODIFICATION` 标记都存在（凭行数核对，缺标记即缺修改）：

```bash
grep -rn 'BLOT MODIFICATION' FlyleafLib/ FlyleafLib.Controls.WPF/
```

行数对不上就是有修改丢失了——按上面清单逐项核对。**只剩部分修改**比全没有更难发现，因为播放看起来能跑、进度条也会动一点，但播到录制端尾巴时会静默停掉；而缺 #6 则快进退/逐帧仍会 av_read_frame 闪退。

### 4. 验证已合并的上游修改

如果升级版本已包含 fix #688 (`c2fbb55`)，跳过即可。否则按「已合并的上游修改」章节重新应用。

### 5. 测试

- [ ] 播放正在录制的 TS 文件（进度条自动更新）
- [ ] 播放到录制中的 TS 文件末尾（不中断，等待新数据）
- [ ] 播放已完成的 TS 文件（正常结束）
- [ ] 快进退 + 逐帧快速/重叠操作，连续反复（不再 av_read_frame 闪退）【验证修改 #6】
- [ ] 绿色线条是否出现
- [ ] 字幕显示（如果有）
