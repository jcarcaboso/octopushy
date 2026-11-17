using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Octopushy.Server.Store;

namespace Octopushy.Server.Auth;

/// <summary>
/// Encrypts the secret of each data-protection key, which signs session and known-browser cookies, with AES-256-GCM
/// under a key derived from the KEK. ASP.NET writes the decryptor's type name into the key file and creates it with
/// the constructor that takes the service provider.
/// </summary>
public sealed class KeyRingEncryptor(IServiceProvider services) : IXmlEncryptor
{
    public EncryptedXmlInfo Encrypt(XElement plaintextElement) =>
        new(KeyRingCipher.Seal(services.GetRequiredService<KeyEncryptionKey>(), plaintextElement), typeof(KeyRingDecryptor));
}

public sealed class KeyRingDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement) =>
        KeyRingCipher.Open(services.GetRequiredService<KeyEncryptionKey>(), encryptedElement);
}

public static class KeyRingCipher
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly XName Element = XName.Get("encryptedKey", "urn:octopushy:key-ring:v1");
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("octopushy/v1 data-protection key ring");
    private static readonly byte[] Associated = Encoding.UTF8.GetBytes("octopushy/v1\0data-protection");

    // How ASP.NET marks a secret in a key file, and what it puts in its place once encrypted.
    private static readonly XNamespace DataProtection = "http://schemas.asp.net/2015/03/dataProtection";
    private static readonly XName RequiresEncryption = DataProtection + "requiresEncryption";
    private static readonly XName EncryptedSecret = DataProtection + "encryptedSecret";

    public static XElement Seal(KeyEncryptionKey kek, XElement plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext.ToString(SaveOptions.DisableFormatting));
        var output = new byte[NonceSize + data.Length + TagSize];
        RandomNumberGenerator.Fill(output.AsSpan(0, NonceSize));
        var key = Derive(kek);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(output.AsSpan(0, NonceSize), data, output.AsSpan(NonceSize, data.Length), output.AsSpan(NonceSize + data.Length), Associated);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(data);
        }

        return new XElement(Element, Convert.ToBase64String(output));
    }

    public static XElement Open(KeyEncryptionKey kek, XElement encrypted)
    {
        if (encrypted.Name != Element)
        {
            throw new CryptographicException("This is not an Octopushy key-ring secret.");
        }

        var input = Convert.FromBase64String(encrypted.Value);
        if (input.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("The key-ring secret is too short.");
        }

        var length = input.Length - NonceSize - TagSize;
        var data = new byte[length];
        var key = Derive(kek);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(input.AsSpan(0, NonceSize), input.AsSpan(NonceSize, length), input.AsSpan(NonceSize + length), data, Associated);
            return XElement.Parse(Encoding.UTF8.GetString(data));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(data);
        }
    }

    private static byte[] Derive(KeyEncryptionKey kek) => HKDF.DeriveKey(HashAlgorithmName.SHA256, kek.Bytes, 32, info: Info);

    /// <summary>
    /// Encrypts, in place, every key an earlier release stored in plain text, and says which keys the KEK cannot open.
    /// The keys stay the same, so the cookies they signed stay valid. Each file is replaced by a rename, so a crash
    /// leaves either the old file or the new one. Runs at start, before the ring is first read.
    /// </summary>
    public static void EncryptExisting(string directory, KeyEncryptionKey kek, ILogger logger)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.xml"))
        {
            var document = XElement.Load(file);
            foreach (var secret in document.Descendants(EncryptedSecret).SelectMany(e => e.Elements()).Where(e => e.Name == Element))
            {
                try
                {
                    Open(kek, secret);
                }
                catch (CryptographicException)
                {
                    logger.LogError(
                        "The session key {File} cannot be decrypted with the current key-encryption key. Sessions it signed are refused, and a new key will be made. Restoring the original key-encryption key makes it readable again.",
                        file);
                }
            }

            var plain = document.Descendants().Where(e => (string?)e.Attribute(RequiresEncryption) == "true").ToList();
            if (plain.Count == 0)
            {
                continue;
            }

            foreach (var secret in plain)
            {
                secret.ReplaceWith(new XElement(EncryptedSecret,
                    new XAttribute("decryptorType", typeof(KeyRingDecryptor).AssemblyQualifiedName!),
                    Seal(kek, secret)));
            }

            var temporary = file + ".tmp";
            using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = OperatingSystem.IsWindows() ? null : UnixFileMode.UserRead | UnixFileMode.UserWrite,
            }))
            {
                document.Save(stream);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, file, overwrite: true);
            logger.LogInformation("Encrypted the session key {File} with a key derived from the key-encryption key.", file);
        }
    }
}
