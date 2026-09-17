using SharpGen.Runtime;
using Vortice.DXGI;

using ResultCode = Vortice.DXGI.ResultCode;

using FlyleafLib.MediaFramework.MediaFrame;

namespace FlyleafLib.MediaFramework.MediaRenderer;

public unsafe partial class Renderer
{
    long            renderRequestAt, lastRenderAt;
    volatile bool   canIdle;
    volatile bool   isIdleRunning;
    internal object lockRenderLoops = new(); // BLOT (Q-0319): internal so SwapChain teardown can share the render lock


    internal void RenderRequest(VideoFrame frame = null, bool forceClear = false)
    {
        lock (lockRenderLoops)
        {
            renderRequestAt = DateTime.UtcNow.Ticks;

            if ((frame != null || forceClear))
                Frames.SetRendererFrame(frame);

            if (!SwapChain.CanPresent || !canIdle || isIdleRunning)
                return;

            isIdleRunning = true;
        }

        Task.Run(RenderIdleLoop);
    }
    internal void RenderIdleStart(bool force = false)
    {
        lock (lockRenderLoops)
        {
            canIdle = true;
            if (force)
                renderRequestAt = DateTime.UtcNow.Ticks;

            // TBR: Check if last timestamp?* to start idle
            if (renderRequestAt > lastRenderAt)
                RenderRequest();
        }
    }
    internal void RenderIdleStop()
    {
        canIdle = false;
        while (isIdleRunning)
            { canIdle = false; Thread.Sleep(1); }
    }
    void RenderIdleLoop()
    {
        int rechecks = 1000; // Awake for ~5sec when Idle
        while (SwapChain.CanPresent)
        {
            while (renderRequestAt <= lastRenderAt && rechecks-- > 0)
            {
                if (!canIdle || !SwapChain.CanPresent)
                    { rechecks = 0; break; }

                Thread.Sleep(5); // might not TimeBeginPeriod1 (can drop fps or slow down cancelation)
            }

            if (rechecks < 1)
                break;

            rechecks = 1000;
            RenderIdle();
        }

        lock (lockRenderLoops) // To avoid race condition*?
        {
            isIdleRunning = false;
            if (renderRequestAt > lastRenderAt && canIdle && SwapChain.CanPresent)
                RenderRequest();
        }
    }
    bool RenderIdle()
    {
        try
        {
            lastRenderAt = DateTime.UtcNow.Ticks;

            if (!SwapChain.CanPresent)
                return true;

            lock (lockRenderLoops)
            {
                // BLOT MODIFICATION (Q-0319): re-check under the render lock — the swap
                // chain may have been disposed while we waited for the lock (see RenderPlay).
                if (SwapChain.Disposed || !SwapChain.CanPresent)
                    return true;

                bool needsClear = true;
                if (VideoProcessor == VideoProcessors.D3D11)
                {
                    D3ProcessRequests();

                    if (VideoProcessor != VideoProcessors.D3D11)
                        return RenderIdle();

                    if (!d3CanPresent)
                        return true;

                    if (Frames.RendererFrame != null)
                        { D3Render(Frames.RendererFrame, false); needsClear = false; }
                }
                else
                {
                    FLProcessRequests();

                    if (VideoProcessor == VideoProcessors.D3D11)
                        return RenderIdle();

                
                    if (Frames.RendererFrame != null)
                        { FLRender(Frames.RendererFrame); needsClear = false; }
                }

                if (needsClear)
                {
                    if (!Config.Video.ClearScreen)
                        return true;

                    //SubsDispose();
                    context.OMSetRenderTargets(SwapChain.BackBufferRtv);
                    context.ClearRenderTargetView(SwapChain.BackBufferRtv, ucfg.flBackColor);
                }
            }

            SwapChain.Present(1, PresentFlags.None);

            return true;
        }
        catch (SharpGenException e)
        {
            Log.Error($"[RenderIdle] Device Lost ({e.ResultCode.NativeApiCode} ({e.ResultCode}) | {device.DeviceRemovedReason} | {e.Message})");
            isIdleRunning = false; // TBR: Possible to call RenderIdleStop which will freeze it #681
            ResetLocal();

            return false;
        }
        catch (Exception e)
        {
            Log.Error($"[RenderIdle] Failed ({e.Message})");

            return false;
        }
    }

    internal bool RefreshPlay(bool secondField) // TODO secondfield embedded*
    {   // Tries to keep ~60fps refreshes within/during playback
        if (lastRenderAt >= renderRequestAt)
            return false;

        RenderIdle();

        return true;
    }
    internal bool RenderPlay(VideoFrame frame, bool secondField)
    {
        try
        {
            lastRenderAt = DateTime.UtcNow.Ticks;

            if (!SwapChain.CanPresent)
            {
                // BLOT (Q-0411): also keep the cached renderer frame fresh while the swap chain
                // is merely *detached* (another player owns the host). Without this the frame
                // freezes at the moment of the detach, and re-attaching renders that stale
                // picture until the next decoded frame arrives — visible as a jump/flicker.
                // Swapping the frame reference is cheap (one lock + assignment, no GPU work);
                // rendering itself stays off because CanPresent is false either way.
                if (Config.Player.SnapshotAlways || SwapChain.IsDetached)
                    lock (lockRenderLoops)
                    {
                        if (VideoProcessor == VideoProcessors.D3D11)
                            D3ProcessRequests();
                        else
                            FLProcessRequests();

                        Frames.SetRendererFrame(frame);

                    }

                return true;
            }
            
            lock (lockRenderLoops)
            {
                // BLOT MODIFICATION (Q-0319): re-check under the render lock — the swap
                // chain may have been disposed while we waited for the lock (we passed
                // the CanPresent check before FlyleafHost.SetPlayer flipped it).
                if (SwapChain.Disposed || !SwapChain.CanPresent)
                    return true;

                if (VideoProcessor == VideoProcessors.D3D11)
                {
                    D3ProcessRequests();

                    if (VideoProcessor != VideoProcessors.D3D11)
                        return RenderPlay(frame, secondField);

                    if (!d3CanPresent)
                        return true;

                    D3Render(frame, secondField);
                }
                else
                {
                    FLProcessRequests();

                    if (VideoProcessor == VideoProcessors.D3D11)
                        return RenderPlay(frame, secondField);

                    FLRender(frame);
                }

                Frames.SetRendererFrame(frame);
            }

            return true;
        }
        catch (SharpGenException e)
        {
            Log.Error($"[RenderPlay] Device Lost ({e.ResultCode.NativeApiCode} ({e.ResultCode}) | {device.DeviceRemovedReason} | {e.Message})");
            ResetLocal(pausePlayer: false);

            return false;
        }
        catch (Exception e)
        {
            Log.Error($"[RenderPlay] Failed ({e.Message})");

            return false;
        }

    }
    /// <summary>
    /// BLOT (Q-0411): Reattach 后立即用当前缓存帧渲染并 Present 一次。
    /// 隐藏期间播放线程只更新 Frames.RendererFrame（不 Present），swapchain 前缓冲
    /// 停留在「上次显示的最后一帧」；DComp 挂回后、播放线程下一次 Present 之前
    /// （实测 6~15ms，再撞上 DWM 合成对齐会被整帧采样），这帧旧内容就会上屏 ——
    /// 即用户看到的「旧画面闪过」。在 Reattach 的同一 UI 操作内同步渲染
    /// （配合隐藏期间持续更新 RendererFrame），让挂上的瞬间前缓冲就是最新画面。
    /// 任何失败只记日志，不影响 Reattach 主流程。
    /// </summary>
    internal void RenderCurrentFrameNow()
    {
        try
        {
            lock (lockRenderLoops)
            {
                if (SwapChain.Disposed || !SwapChain.CanPresent)
                    return;

                // 先处理 pending 的 VP Resize（Reattach 里刚 VPRequest 过），按新尺寸渲染
                if (VideoProcessor == VideoProcessors.D3D11)
                {
                    D3ProcessRequests();
                    if (VideoProcessor != VideoProcessors.D3D11 || !d3CanPresent)
                        return; // VP 切换中，交还给播放线程

                    if (Frames.RendererFrame != null)
                        D3Render(Frames.RendererFrame, false);
                }
                else
                {
                    FLProcessRequests();
                    if (VideoProcessor == VideoProcessors.D3D11)
                        return;

                    if (Frames.RendererFrame != null)
                        FLRender(Frames.RendererFrame);
                }

                // 无帧可渲染（如回看重开前缓存帧已被清）：清屏 ——
                // 绝不让上一次显示的旧画面留在前缓冲被挂上显示。
                if (Frames.RendererFrame == null)
                {
                    context.OMSetRenderTargets(SwapChain.BackBufferRtv);
                    context.ClearRenderTargetView(SwapChain.BackBufferRtv, ucfg.flBackColor);
                }
            }

            SwapChain.Present(1, PresentFlags.None);
        }
        catch (Exception e)
        {
            Log.Error($"[RenderCurrentFrameNow] {e.Message}");
        }
    }
    internal bool PresentPlay()
    {
        try
        {
            if (SwapChain.CanPresent) // TODO: dont present if we didnt render (or d3d11 check can present too)
            {
                SwapChain.Present().CheckError();

            }

            return true;
        }
        catch (SharpGenException e)
        {
            if (e.ResultCode == ResultCode.WasStillDrawing) // For DoNotWait (any reason to still support it with Config?)
            {
                Log.Info($"[V] Frame Dropped (GPU)");
                return false;
            }

            Log.Error($"[PresentPlay] {e.ResultCode.NativeApiCode} ({e.ResultCode}) | {device.DeviceRemovedReason} | {e.Message}");
            ResetLocal(pausePlayer: false);

            return false;
        }
        catch (Exception e)
        {
            Log.Error($"[PresentPlay] Failed ({e.Message})");
            throw; // Force Playback Stop
        }
    }

    public void ClearScreen(bool force = false, bool rendererFrame = true)
    {
        if (force)
        {
            lock (lockDevice)
            {
                if (SwapChain.Disposed)
                    return;

                lock (lockRenderLoops)
                {
                    if (rendererFrame)
                        Frames.SetRendererFrame(null);
                    SubsDispose();
                    context.OMSetRenderTargets(SwapChain.BackBufferRtv);
                    context.ClearRenderTargetView(SwapChain.BackBufferRtv, ucfg.flBackColor);
                }

                SwapChain.Present(1, PresentFlags.None);
            }
        }
        else if (Config.Video.ClearScreen)
            RenderRequest(null, true);
    }
}

