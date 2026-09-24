namespace FlyleafLib.MediaFramework.MediaDemuxer;

public unsafe class CustomIOContext
{
    AVIOContext* avioCtx;
    public Stream stream;
    readonly Demuxer demuxer;

    public CustomIOContext(Demuxer demuxer)
    {
        this.demuxer = demuxer;
    }

    public void Initialize(Stream stream)
    {
        this.stream = stream;
        //this.stream.Seek(0, SeekOrigin.Begin);

        ioread = IORead;
        ioseek = IOSeek;
        avioCtx = avio_alloc_context((byte*)av_malloc((nuint)demuxer.Config.IOStreamBufferSize), demuxer.Config.IOStreamBufferSize, 0, null, ioread, null, ioseek);
        demuxer.FormatContext->pb = avioCtx;
        demuxer.FormatContext->flags |= FmtFlags2.CustomIo;
    }

    public void Dispose()
    {
        if (avioCtx != null)
        {
            av_free(avioCtx->buffer);
            fixed (AVIOContext** ptr = &avioCtx) avio_context_free(ptr);
        }
        avioCtx = null;
        stream = null;
        ioread = null;
        ioseek = null;
    }

    avio_alloc_context_read_packet ioread;
    avio_alloc_context_seek ioseek;

    int IORead(void* opaque, byte* buffer, int bufferSize)
    {
        // BLOT MODIFICATION START: Never let managed exceptions cross the native boundary
        // (av_read_frame -> AVIOCallback -> here). A throw unwinds through FFmpeg native frames and
        // fail-fasts the process when the underlying Stream is torn down mid-flight during rapid
        // seeks/frame-stepping. Degrade to AVERROR_EXIT instead.
        // See FLYLEAF_MODIFICATIONS.md (mod #5)
        try
        {
            int ret;

            if (stream == null) return AVERROR_EXIT; // disposed mid-flight -> polite error, not a crash

            if (demuxer.Interrupter.ShouldInterrupt(null) != 0) return AVERROR_EXIT;

            ret = demuxer.CustomIOContext.stream.Read(new Span<byte>(buffer, bufferSize));

            if (ret > 0)
                return ret;

            if (ret == 0)
                return AVERROR_EOF;

            demuxer.Log.Warn("CustomIOContext Interrupted");

            return AVERROR_EXIT;
        }
        catch (Exception e)
        {
            try { demuxer?.Log?.Warn($"CustomIOContext IORead failed: {e.Message}"); } catch { }
            return AVERROR_EXIT;
        }
        // BLOT MODIFICATION END
    }

    long IOSeek(void* opaque, long offset, IOSeekFlags whence)
    {
        // BLOT MODIFICATION START: see IORead - exceptions must never cross the native boundary
        // See FLYLEAF_MODIFICATIONS.md (mod #5)
        try
        {
            //System.Diagnostics.Debug.WriteLine($"** S | {decCtx.demuxer.fmtCtx->pb->pos} - {decCtx.demuxer.ioStream.Position}");

            return whence == IOSeekFlags.Size
                ? demuxer.CustomIOContext.stream.Length
                : demuxer.CustomIOContext.stream.Seek(offset, (SeekOrigin)whence);
        }
        catch (Exception e)
        {
            try { demuxer?.Log?.Warn($"CustomIOContext IOSeek failed: {e.GetType().Name}: {e.Message}"); } catch { }
            // SEEK to a position failed -> report -1 (AVSEEK fail) so FFmpeg does NOT read from a
            // guessed/wrong offset (position corruption is worse than a hard error). Only the
            // "Size" probe legitimately returns the stream length; null stream -> 0.
            return whence == IOSeekFlags.Size ? (demuxer?.CustomIOContext?.stream?.Length ?? 0) : -1;
        }
        // BLOT MODIFICATION END
    }
}
