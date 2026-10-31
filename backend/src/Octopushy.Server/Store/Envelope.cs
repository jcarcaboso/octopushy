using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Server.Data;
using Octopushy.Server.Options;

namespace Octopushy.Server.Store;

/// <summary>
/// The key-encryption key: 32 random bytes in a file only the server can read, outside the
/// database and anything backed up with it. Initialized only for an empty installation.
/// </summary>
public sealed class KeyEncryptionKey
{
    public const int Size = 32;

    public KeyEncryptionKey(IOptions<OctopushyOptions> options, ILogger<KeyEncryptionKey> logger)
    {
        Bytes = Load(PathFor(options.Value), logger);
    }

    internal KeyEncryptionKey(byte[] bytes)
    {
        Bytes = bytes;
    }

    internal byte[] Bytes { get; }

    private static string PathFor(OctopushyOptions options) => options.Store.KeyFile ?? Path.Combine(options.DataDirectory, "keys", "kek");

    /// <summary>Must run after schema migration and before any encrypted store is opened.</summary>
    internal static async Task InitializeAsync(OctopushyDb db, OctopushyOptions options, ILogger logger, CancellationToken ct)
    {
        var path = PathFor(options);
        WarnIfBackedUpWithData(path, options, logger);
        if (File.Exists(path))
        {
            _ = Load(path, logger);
            return;
        }

        var artifacts = Path.Combine(options.DataDirectory, "artifacts");
        if (await db.StoreVersions.AnyAsync(ct) || await db.StoreEntries.AnyAsync(ct) || await db.Artifacts.AnyAsync(ct) ||
            await db.TofuStateVersions.AnyAsync(ct) ||
            (Directory.Exists(artifacts) && Directory.EnumerateFiles(artifacts, "*", SearchOption.AllDirectories).Any()))
        {
            throw new InvalidOperationException("The encryption key is missing from an existing installation. Restore its original key; a new key cannot recover stored values, OpenTofu state or artifacts.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = OperatingSystem.IsWindows() ? null : UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        stream.Write(RandomNumberGenerator.GetBytes(Size));
        stream.Flush(flushToDisk: true);
        logger.LogWarning("Initialized the encryption key for an empty installation at {Path}. Retain it separately from database backups.", path);
    }

    /// <summary>A backup of the data directory must not also hold the key that decrypts the store, which includes the agent CA's private key.</summary>
    internal static void WarnIfBackedUpWithData(string path, OctopushyOptions options, ILogger logger)
    {
        // Lexical only: a symlink that puts the key inside the data directory is not detected.
        var data = Path.GetFullPath(options.DataDirectory);
        if (!data.EndsWith(Path.DirectorySeparatorChar))
        {
            data += Path.DirectorySeparatorChar;
        }

        if (Path.GetFullPath(path).StartsWith(data, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "The key-encryption key {Path} is inside the data directory, so a copy of the data directory plus the database can decrypt every stored secret and the agent CA key. Set Octopushy:Store:KeyFile to a path outside it, such as a container secret.",
                path);
        }
    }

    private static byte[] Load(string path, ILogger logger)
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

        throw new InvalidOperationException("The encryption key is missing. Initialize an empty installation or restore its original key.");
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
