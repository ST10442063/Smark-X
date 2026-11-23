using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SmartX.Core.Security;

/// <summary>
/// The on-disk format for encrypted sensor attachments: AES-256-GCM applied in
/// fixed-size chunks.
/// <para>
/// <b>Why chunked rather than one GCM call.</b> A single AES-GCM operation needs
/// the whole plaintext in memory at once, which would undo the upload pipeline's
/// streaming - every 10 MB log would become a 10 MB array again. Splitting the file
/// into 64 KB chunks and sealing each one separately keeps memory flat while still
/// giving every byte both confidentiality and integrity.
/// </para>
/// <para>
/// <b>Why chunking is still safe.</b> Naively sealed chunks could be reordered,
/// dropped or truncated without any single tag failing. Three things prevent that:
/// each chunk's nonce ends in its sequence number, so a moved chunk decrypts under
/// the wrong nonce; each chunk's length and "final" flag are authenticated as
/// associated data; and the reader refuses a file that ends before a chunk marked
/// final, or carries anything after one.
/// </para>
/// <code>
/// file   = header chunk*
/// header = "SXE1" | chunkSize (int32 LE) | noncePrefix (8 random bytes)
/// chunk  = lengthAndFlag (uint32 LE, high bit = final) | ciphertext | tag (16 bytes)
/// nonce  = noncePrefix | chunkIndex (uint32 BE)
/// </code>
/// </summary>
public static class AttachmentEncryption
{
    /// <summary>Name reported to clients for the scheme in use.</summary>
    public const string Algorithm = "AES-256-GCM";

    /// <summary>Required key length in bytes (256 bits).</summary>
    public const int KeySizeBytes = 32;

    /// <summary>Plaintext bytes sealed per chunk.</summary>
    public const int DefaultChunkSize = 64 * 1024;

    /// <summary>Bytes in the file header.</summary>
    public const int HeaderSize = 16;

    /// <summary>Bytes in each chunk's length-and-flag prefix.</summary>
    public const int ChunkHeaderSize = 4;

    /// <summary>Bytes in each chunk's authentication tag.</summary>
    public const int TagSize = 16;

    internal const int NoncePrefixSize = 8;
    internal const int NonceSize = 12;
    internal const uint FinalFlag = 0x8000_0000;

    /// <summary>
    /// Upper bound on the chunk size a reader will accept from a header, so a
    /// corrupted header cannot make the reader allocate an enormous buffer.
    /// </summary>
    internal const int MaxChunkSize = 1024 * 1024;

    internal static ReadOnlySpan<byte> Magic => "SXE1"u8;

    /// <summary>Generates a fresh random 256-bit key.</summary>
    public static byte[] GenerateKey() => RandomNumberGenerator.GetBytes(KeySizeBytes);

    /// <summary>Throws when a key is not exactly 256 bits.</summary>
    internal static void EnsureValidKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySizeBytes)
        {
            throw new ArgumentException(
                $"An attachment key must be {KeySizeBytes} bytes; this one is {key.Length}.",
                nameof(key));
        }
    }

    /// <summary>Builds the nonce for one chunk from the file prefix and chunk index.</summary>
    internal static void ComposeNonce(ReadOnlySpan<byte> prefix, uint chunkIndex, Span<byte> nonce)
    {
        prefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[NoncePrefixSize..], chunkIndex);
    }
}

/// <summary>
/// A write-only stream that encrypts everything written to it into the
/// <see cref="AttachmentEncryption"/> format on the wrapped stream.
/// <para>
/// Call <see cref="FinishAsync"/> once the last byte is written. Disposing without
/// finishing deliberately leaves the output without a final chunk, so a cancelled
/// or failed upload can never be mistaken for a complete file when it is read back.
/// </para>
/// </summary>
public sealed class AesGcmEncryptingStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly AesGcm _aes;
    private readonly int _chunkSize;
    private readonly byte[] _noncePrefix = RandomNumberGenerator.GetBytes(AttachmentEncryption.NoncePrefixSize);
    private readonly byte[] _plain;
    private readonly byte[] _sealed;
    private readonly byte[] _nonce = new byte[AttachmentEncryption.NonceSize];

    private int _buffered;
    private uint _chunkIndex;
    private bool _headerWritten;
    private bool _finished;
    private bool _disposed;

    /// <summary>Creates an encrypting stream over <paramref name="inner"/>.</summary>
    /// <param name="inner">Destination for the ciphertext.</param>
    /// <param name="key">256-bit key.</param>
    /// <param name="chunkSize">Plaintext bytes per sealed chunk.</param>
    /// <param name="leaveOpen">Whether disposing this stream leaves <paramref name="inner"/> open.</param>
    public AesGcmEncryptingStream(
        Stream inner,
        ReadOnlySpan<byte> key,
        int chunkSize = AttachmentEncryption.DefaultChunkSize,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        AttachmentEncryption.EnsureValidKey(key);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkSize, AttachmentEncryption.MaxChunkSize);

        if (!inner.CanWrite)
        {
            throw new ArgumentException("The destination stream must be writable.", nameof(inner));
        }

        _inner = inner;
        _leaveOpen = leaveOpen;
        _chunkSize = chunkSize;
        _aes = new AesGcm(key, AttachmentEncryption.TagSize);
        _plain = new byte[chunkSize];
        _sealed = new byte[AttachmentEncryption.ChunkHeaderSize + chunkSize + AttachmentEncryption.TagSize];
    }

    /// <summary>Plaintext bytes accepted so far.</summary>
    public long PlaintextLength { get; private set; }

    /// <summary>Ciphertext bytes written to the inner stream so far, header included.</summary>
    public long CiphertextLength { get; private set; }

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => !_disposed && !_finished;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => PlaintextLength;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
        => Write(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureWritable();

        while (!buffer.IsEmpty)
        {
            // A full buffer is only sealed once more data proves it is not the last
            // chunk. Sealing eagerly would force an empty trailing chunk whenever a
            // file length lands exactly on a chunk boundary.
            if (_buffered == _chunkSize)
            {
                var length = SealChunk(isFinal: false);
                WriteHeaderIfNeeded();
                _inner.Write(_sealed, 0, length);
                CiphertextLength += length;
            }

            var take = Math.Min(buffer.Length, _chunkSize - _buffered);
            buffer[..take].CopyTo(_plain.AsSpan(_buffered));
            _buffered += take;
            PlaintextLength += take;
            buffer = buffer[take..];
        }
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureWritable();

        while (!buffer.IsEmpty)
        {
            if (_buffered == _chunkSize)
            {
                var length = SealChunk(isFinal: false);
                await WriteHeaderIfNeededAsync(cancellationToken);
                await _inner.WriteAsync(_sealed.AsMemory(0, length), cancellationToken);
                CiphertextLength += length;
            }

            var take = Math.Min(buffer.Length, _chunkSize - _buffered);
            buffer.Span[..take].CopyTo(_plain.AsSpan(_buffered));
            _buffered += take;
            PlaintextLength += take;
            buffer = buffer[take..];
        }
    }

    /// <summary>
    /// Seals whatever is buffered as the final chunk and flushes the inner stream.
    /// Nothing more can be written afterwards.
    /// </summary>
    public async Task FinishAsync(CancellationToken cancellationToken = default)
    {
        EnsureWritable();

        var length = SealChunk(isFinal: true);
        await WriteHeaderIfNeededAsync(cancellationToken);
        await _inner.WriteAsync(_sealed.AsMemory(0, length), cancellationToken);
        CiphertextLength += length;

        _finished = true;
        await _inner.FlushAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override void Flush()
    {
        // Buffered plaintext is intentionally not flushed early - a chunk is only
        // sealed once it is full or final, which is what keeps the format fixed-size.
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            ReleaseResources();

            if (!_leaveOpen)
            {
                _inner.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            ReleaseResources();

            if (!_leaveOpen)
            {
                await _inner.DisposeAsync();
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Encrypts the buffered plaintext into <see cref="_sealed"/> and returns its length.</summary>
    private int SealChunk(bool isFinal)
    {
        var chunkHeader = _sealed.AsSpan(0, AttachmentEncryption.ChunkHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(
            chunkHeader,
            (uint)_buffered | (isFinal ? AttachmentEncryption.FinalFlag : 0u));

        AttachmentEncryption.ComposeNonce(_noncePrefix, _chunkIndex, _nonce);

        _aes.Encrypt(
            _nonce,
            _plain.AsSpan(0, _buffered),
            _sealed.AsSpan(AttachmentEncryption.ChunkHeaderSize, _buffered),
            _sealed.AsSpan(AttachmentEncryption.ChunkHeaderSize + _buffered, AttachmentEncryption.TagSize),
            associatedData: chunkHeader);

        var length = AttachmentEncryption.ChunkHeaderSize + _buffered + AttachmentEncryption.TagSize;

        CryptographicOperations.ZeroMemory(_plain.AsSpan(0, _buffered));
        _buffered = 0;
        _chunkIndex = checked(_chunkIndex + 1);

        return length;
    }

    private byte[] BuildHeader()
    {
        var header = new byte[AttachmentEncryption.HeaderSize];
        AttachmentEncryption.Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), _chunkSize);
        _noncePrefix.CopyTo(header, 8);
        return header;
    }

    private void WriteHeaderIfNeeded()
    {
        if (_headerWritten)
        {
            return;
        }

        _inner.Write(BuildHeader());
        CiphertextLength += AttachmentEncryption.HeaderSize;
        _headerWritten = true;
    }

    private async ValueTask WriteHeaderIfNeededAsync(CancellationToken cancellationToken)
    {
        if (_headerWritten)
        {
            return;
        }

        await _inner.WriteAsync(BuildHeader(), cancellationToken);
        CiphertextLength += AttachmentEncryption.HeaderSize;
        _headerWritten = true;
    }

    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_finished)
        {
            throw new InvalidOperationException("The encrypted stream has already been finished.");
        }
    }

    private void ReleaseResources()
    {
        CryptographicOperations.ZeroMemory(_plain);
        _aes.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// A read-only stream that decrypts a file in the <see cref="AttachmentEncryption"/>
/// format one chunk at a time.
/// <para>
/// Only one chunk of plaintext is ever held in memory. A chunk that fails its
/// authentication tag, a file cut short before its final chunk, or bytes appended
/// after it all raise a <see cref="CryptographicException"/> rather than returning
/// data an attacker has altered.
/// </para>
/// </summary>
public sealed class AesGcmDecryptingStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly AesGcm _aes;
    private readonly byte[] _noncePrefix = new byte[AttachmentEncryption.NoncePrefixSize];
    private readonly byte[] _nonce = new byte[AttachmentEncryption.NonceSize];
    private readonly byte[] _chunkHeader = new byte[AttachmentEncryption.ChunkHeaderSize];
    private readonly byte[] _tag = new byte[AttachmentEncryption.TagSize];

    private byte[] _cipher = [];
    private byte[] _plain = [];
    private int _plainOffset;
    private int _plainLength;
    private int _chunkSize;
    private uint _chunkIndex;
    private bool _headerRead;
    private bool _finalSeen;
    private bool _disposed;

    /// <summary>Creates a decrypting stream over <paramref name="inner"/>.</summary>
    /// <param name="inner">Source of the ciphertext.</param>
    /// <param name="key">256-bit key the file was encrypted with.</param>
    /// <param name="leaveOpen">Whether disposing this stream leaves <paramref name="inner"/> open.</param>
    public AesGcmDecryptingStream(Stream inner, ReadOnlySpan<byte> key, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        AttachmentEncryption.EnsureValidKey(key);

        if (!inner.CanRead)
        {
            throw new ArgumentException("The source stream must be readable.", nameof(inner));
        }

        _inner = inner;
        _leaveOpen = leaveOpen;
        _aes = new AesGcm(key, AttachmentEncryption.TagSize);
    }

    /// <inheritdoc />
    public override bool CanRead => !_disposed;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (_plainOffset == _plainLength)
        {
            if (_finalSeen)
            {
                return 0;
            }

            // Synchronous callers get synchronous I/O. The shared routine completes
            // without awaiting anything when useAsync is false.
            var pending = LoadNextChunkAsync(useAsync: false, CancellationToken.None);
            if (!pending.IsCompleted)
            {
                throw new InvalidOperationException("Synchronous chunk load unexpectedly went asynchronous.");
            }

            pending.GetAwaiter().GetResult();
        }

        return CopyOut(buffer);
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (_plainOffset == _plainLength)
        {
            if (_finalSeen)
            {
                return 0;
            }

            await LoadNextChunkAsync(useAsync: true, cancellationToken);
        }

        return CopyOut(buffer.Span);
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            ReleaseResources();

            if (!_leaveOpen)
            {
                _inner.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            ReleaseResources();

            if (!_leaveOpen)
            {
                await _inner.DisposeAsync();
            }
        }

        GC.SuppressFinalize(this);
    }

    private int CopyOut(Span<byte> destination)
    {
        var take = Math.Min(destination.Length, _plainLength - _plainOffset);
        _plain.AsSpan(_plainOffset, take).CopyTo(destination);
        _plainOffset += take;
        return take;
    }

    private async ValueTask LoadNextChunkAsync(bool useAsync, CancellationToken cancellationToken)
    {
        if (!_headerRead)
        {
            var header = new byte[AttachmentEncryption.HeaderSize];
            if (await FillAsync(header, useAsync, cancellationToken) != header.Length)
            {
                throw new CryptographicException("The attachment is too short to be an encrypted file.");
            }

            if (!header.AsSpan(0, 4).SequenceEqual(AttachmentEncryption.Magic))
            {
                throw new CryptographicException("The attachment is not in the Smart-X encrypted format.");
            }

            _chunkSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            if (_chunkSize < 1 || _chunkSize > AttachmentEncryption.MaxChunkSize)
            {
                throw new CryptographicException("The encrypted attachment declares an invalid chunk size.");
            }

            header.AsSpan(8, AttachmentEncryption.NoncePrefixSize).CopyTo(_noncePrefix);
            _cipher = new byte[_chunkSize];
            _plain = new byte[_chunkSize];
            _headerRead = true;
        }

        var headerBytes = await FillAsync(_chunkHeader, useAsync, cancellationToken);
        if (headerBytes != _chunkHeader.Length)
        {
            // Running out of data here, before any chunk said it was the last, means
            // the file was cut short - the one tamper a per-chunk tag cannot see.
            throw new CryptographicException("The encrypted attachment is truncated.");
        }

        var lengthAndFlag = BinaryPrimitives.ReadUInt32LittleEndian(_chunkHeader);
        var isFinal = (lengthAndFlag & AttachmentEncryption.FinalFlag) != 0;
        var length = (int)(lengthAndFlag & ~AttachmentEncryption.FinalFlag);

        if (length > _chunkSize || (!isFinal && length != _chunkSize))
        {
            throw new CryptographicException("The encrypted attachment has a malformed chunk.");
        }

        if (await FillAsync(_cipher.AsMemory(0, length), useAsync, cancellationToken) != length
            || await FillAsync(_tag, useAsync, cancellationToken) != _tag.Length)
        {
            throw new CryptographicException("The encrypted attachment is truncated.");
        }

        AttachmentEncryption.ComposeNonce(_noncePrefix, _chunkIndex, _nonce);

        // Throws AuthenticationTagMismatchException if a single bit of the chunk,
        // its length, its final flag or its position in the file has changed.
        _aes.Decrypt(_nonce, _cipher.AsSpan(0, length), _tag, _plain.AsSpan(0, length), _chunkHeader);

        _plainOffset = 0;
        _plainLength = length;
        _chunkIndex = checked(_chunkIndex + 1);

        if (isFinal)
        {
            var probe = new byte[1];
            if (await FillAsync(probe, useAsync, cancellationToken) != 0)
            {
                throw new CryptographicException("The encrypted attachment has data after its final chunk.");
            }

            _finalSeen = true;
        }
    }

    /// <summary>Reads until <paramref name="buffer"/> is full or the source ends.</summary>
    private async ValueTask<int> FillAsync(Memory<byte> buffer, bool useAsync, CancellationToken cancellationToken)
    {
        var total = 0;

        while (total < buffer.Length)
        {
            var read = useAsync
                ? await _inner.ReadAsync(buffer[total..], cancellationToken)
                : _inner.Read(buffer.Span[total..]);

            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private void ReleaseResources()
    {
        CryptographicOperations.ZeroMemory(_plain);
        _aes.Dispose();
        _disposed = true;
    }
}
