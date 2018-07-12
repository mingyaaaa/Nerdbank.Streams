﻿// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE.txt file in the project root for full license information.

namespace Nerdbank.Streams
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Pipelines;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Microsoft.VisualStudio.Threading;

    /// <summary>
    /// A <see cref="Stream"/> that acts as a queue for bytes, in that what gets written to it
    /// can then be read from it, in order.
    /// </summary>
    public class HalfDuplexStream : Stream, IDisposableObservable
    {
        /// <summary>
        /// The pipe that does all the hard work.
        /// </summary>
        private readonly Pipe pipe;

        /// <summary>
        /// Initializes a new instance of the <see cref="HalfDuplexStream"/> class.
        /// </summary>
        public HalfDuplexStream()
            : this(32 * 1024, 16 * 1024)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="HalfDuplexStream"/> class.
        /// </summary>
        /// <param name="resumeWriterThreshold">The size the buffer must shrink to after hitting <paramref name="pauseWriterThreshold"/> before writing is allowed to resume.</param>
        /// <param name="pauseWriterThreshold">The maximum size the buffer is allowed to grow before write calls are blocked (pending a read that will release buffer space.</param>
        public HalfDuplexStream(int resumeWriterThreshold, int pauseWriterThreshold)
        {
            PipeOptions options = new PipeOptions(
                pauseWriterThreshold: pauseWriterThreshold,
                resumeWriterThreshold: resumeWriterThreshold,
                useSynchronizationContext: false);
            this.pipe = new Pipe(options);
        }

        /// <inheritdoc />
        public bool IsDisposed { get; private set; }

        /// <inheritdoc />
        public override bool CanRead => !this.IsDisposed;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => !this.IsDisposed;

        /// <inheritdoc />
        public override long Length => throw this.ThrowDisposedOr(new NotSupportedException());

        /// <inheritdoc />
        public override long Position
        {
            get => throw this.ThrowDisposedOr(new NotSupportedException());
            set => throw this.ThrowDisposedOr(new NotSupportedException());
        }

        /// <summary>
        /// Signals that no more writing will take place, causing readers to receive 0 bytes when asking for any more data.
        /// </summary>
        public void CompleteWriting() => this.pipe.Writer.Complete();

        /// <inheritdoc />
        public override async Task FlushAsync(CancellationToken cancellationToken) => await this.pipe.Writer.FlushAsync(cancellationToken);

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw this.ThrowDisposedOr(new NotSupportedException());

        /// <inheritdoc />
        public override void SetLength(long value) => throw this.ThrowDisposedOr(new NotSupportedException());

        /// <inheritdoc />
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Requires.NotNull(buffer, nameof(buffer));
            Requires.Range(offset + count <= buffer.Length, nameof(count));
            Requires.Range(offset >= 0, nameof(offset));
            Requires.Range(count > 0, nameof(count));

            ReadResult readResult = await this.pipe.Reader.ReadAsync(cancellationToken);
            int bytesRead = 0;
            System.Buffers.ReadOnlySequence<byte> slice = readResult.Buffer.Slice(0, Math.Min(count, readResult.Buffer.Length));
            foreach (ReadOnlyMemory<byte> span in slice)
            {
                int bytesToCopy = Math.Min(count, span.Length);
                span.CopyTo(new Memory<byte>(buffer, offset, bytesToCopy));
                offset += bytesToCopy;
                count -= bytesToCopy;
                bytesRead += bytesToCopy;
            }

            this.pipe.Reader.AdvanceTo(slice.End);
            return bytesRead;
        }

        /// <inheritdoc />
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Requires.NotNull(buffer, nameof(buffer));
            Requires.Range(offset + count <= buffer.Length, nameof(count));
            Requires.Range(offset >= 0, nameof(offset));
            Requires.Range(count >= 0, nameof(count));
            Verify.NotDisposed(this);

            await this.pipe.Writer.WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count)).ConfigureAwait(false);
        }

#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => this.ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => this.WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

        /// <inheritdoc />
        public override void Flush() => this.FlushAsync().GetAwaiter().GetResult();

#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            this.IsDisposed = true;
            this.pipe.Writer.Complete();
            this.pipe.Reader.Complete();
            base.Dispose(disposing);
        }

        private Exception ThrowDisposedOr(Exception ex)
        {
            Verify.NotDisposed(this);
            throw ex;
        }
    }
}