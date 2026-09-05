using System.Runtime.InteropServices;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using Xunit;

namespace VideoSecurityPlayer.Tests;

public sealed class SeekableStreamMediaInputTests
{
    [Fact]
    public void RequestStop_PreventsNewReadsUntilPlaybackIsPrepared()
    {
        using var source = new MemoryStream([1, 2, 3, 4]);
        using var input = new SeekableStreamMediaInput(source);
        var nativeBuffer = Marshal.AllocHGlobal(4);
        try
        {
            input.RequestStop();
            Assert.Equal(-1, input.Read(nativeBuffer, 4));
            Assert.False(input.Seek(0));
            Assert.False(input.Open(out _));

            input.PrepareForPlayback();
            Assert.True(input.Open(out var size));
            Assert.Equal(4UL, size);
            Assert.Equal(4, input.Read(nativeBuffer, 4));

            var actual = new byte[4];
            Marshal.Copy(nativeBuffer, actual, 0, actual.Length);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, actual);
        }
        finally
        {
            Marshal.FreeHGlobal(nativeBuffer);
        }
    }

    [Fact]
    public async Task RequestStop_DoesNotWaitForActiveReadAndDiscardsItsLateBytes()
    {
        using var source = new BlockingReadStream([7, 8, 9, 10]);
        using var input = new SeekableStreamMediaInput(source);
        var nativeBuffer = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.Copy(new byte[4], 0, nativeBuffer, 4);
            var readTask = Task.Run(() => input.Read(nativeBuffer, 4));
            await source.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            input.RequestStop();
            Assert.False(readTask.IsCompleted);

            source.AllowReadToFinish.TrySetResult();
            Assert.Equal(-1, await readTask.WaitAsync(TimeSpan.FromSeconds(2)));

            var actual = new byte[4];
            Marshal.Copy(nativeBuffer, actual, 0, actual.Length);
            Assert.Equal(new byte[4], actual);
        }
        finally
        {
            source.AllowReadToFinish.TrySetResult();
            Marshal.FreeHGlobal(nativeBuffer);
        }
    }

    private sealed class BlockingReadStream(byte[] data) : Stream
    {
        private long _position;

        public TaskCompletionSource ReadEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowReadToFinish { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadEntered.TrySetResult();
            AllowReadToFinish.Task.GetAwaiter().GetResult();
            var available = (int)Math.Min(count, data.Length - _position);
            data.AsSpan((int)_position, available).CopyTo(buffer.AsSpan(offset, available));
            _position += available;
            return available;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            return Position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
