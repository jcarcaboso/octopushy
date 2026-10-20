using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Octopushy.Server.Options;

namespace Octopushy.Server.Store;

/// <summary>
/// The key-encryption key: 32 random bytes in a file only the server can read, outside the
/// database and anything backed up with it. Created on first start when missing.
/// </summary>
public sealed class KeyEncryptionKey
{
    public const int Size = 32;

    public KeyEncryptionKey(IOptions<OctopushyOptions> options, ILogger<KeyEncryptionKey> logger)
    {
        var path = options.Value.Store.KeyFile ?? Path.Combine(options.Value.DataDirectory, "keys", "kek");
        Bytes = LoadOrCreate(path, logger);
    }

    internal KeyEncryptionKey(byte[] bytes)
    {
        Bytes = bytes;
    }

    internal byte[] Bytes { get; }

    private static byte[] LoadOrCreate(string path, ILogger logger)
    {
        if (File.Exists(path))
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length != Size)
            {
                throw new InvalidOperationException($"The key-encryption key {path} must hold exactly {Size} bytes.");
            }

            // Container secrets (/run/secrets) are guarded by the runtime and the host's directory permissions.
            var containerSecret = path.StartsWith("/run/secrets/", StringComparison.Ordinal);
            if (!containerSecret && !OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) != 0)
            {
                logger.LogWarning("The key-encryption key {Path} is readable by other users; restrict it to the server's user.", path);
            }

            return bytes;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var key = RandomNumberGenerator.GetBytes(Size);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(path, options))
        {
            stream.Write(key);
        }

        logger.LogWarning("Created a new key-encryption key at {Path}. Keep it out of database backups; without it, stored values cannot be read.", path);
        return key;
    }
}

/// <summary>
/// Envelope encryption: each value gets a fresh data key, encrypted under the key-encryption
/// key. Both layers are AES-256-GCM, bound to a context (such as the key name and version), so
/// a ciphertext moved to another key or version fails to decrypt.
/// </summary>
public sealed class Envelope(KeyEncryptionKey kek)
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public (byte[] Ciphertext, byte[] WrappedKey) Seal(ReadOnlySpan<byte> plaintext, string context)
    {
        var dataKey = RandomNumberGenerator.GetBytes(KeyEncryptionKey.Size);
        try
        {
            var ciphertext = Encrypt(dataKey, plaintext, Associated("value", context));
            var wrapped = Encrypt(kek.Bytes, dataKey, Associated("key", context));
            return (ciphertext, wrapped);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public byte[] Open(byte[] ciphertext, byte[] wrappedKey, string context)
    {
        var dataKey = Decrypt(kek.Bytes, wrappedKey, Associated("key", context));
        try
        {
            return Decrypt(dataKey, ciphertext, Associated("value", context));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>Seals into one blob: the wrapped key's length, the wrapped key, then the ciphertext.</summary>
    public byte[] SealBlob(ReadOnlySpan<byte> plaintext, string context)
    {
        var (ciphertext, wrapped) = Seal(plaintext, context);
        var blob = new byte[2 + wrapped.Length + ciphertext.Length];
        blob[0] = (byte)(wrapped.Length >> 8);
        blob[1] = (byte)wrapped.Length;
        wrapped.CopyTo(blob, 2);
        ciphertext.CopyTo(blob, 2 + wrapped.Length);
        return blob;
    }

    public byte[] OpenBlob(byte[] blob, string context)
    {
        var length = (blob[0] << 8) | blob[1];
        return Open(blob[(2 + length)..], blob[2..(2 + length)], context);
    }

    private static byte[] Associated(string layer, string context) =>
        Encoding.UTF8.GetBytes($"octopushy/v1\0{layer}\0{context}");

    private static byte[] Encrypt(byte[] key, ReadOnlySpan<byte> plaintext, byte[] associated)
    {
        var output = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(NonceSize, plaintext.Length), output.AsSpan(NonceSize + plaintext.Length), associated);
        return output;
    }

    private static byte[] Decrypt(byte[] key, byte[] input, byte[] associated)
    {
        if (input.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("The ciphertext is too short.");
        }

        var length = input.Length - NonceSize - TagSize;
        var plaintext = new byte[length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(input.AsSpan(0, NonceSize), input.AsSpan(NonceSize, length), input.AsSpan(NonceSize + length), plaintext, associated);
        return plaintext;
    }
}
