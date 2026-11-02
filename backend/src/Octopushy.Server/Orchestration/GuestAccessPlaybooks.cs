using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;

namespace Octopushy.Server.Orchestration;

/// <summary>
/// The playbooks a rotation runs. They ship with the server and not in a lab repository, so that
/// removing a key is the same exact-match code on every guest, reviewed here, and a repository
/// cannot change what "the old controller-owned key" means.
/// </summary>
public static class GuestAccessPlaybooks
{
    public const string Root = "guest-access";
    public const string Authorize = "authorize.yml";
    public const string Verify = "verify.yml";
    public const string Remove = "remove.yml";

    /// <summary>The files a rotation's steps receive in place of a repository snapshot.</summary>
    public static byte[] Bundle()
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            var assembly = Assembly.GetExecutingAssembly();
            foreach (var name in new[] { Authorize, Verify, Remove })
            {
                using var content = assembly.GetManifestResourceStream($"{Root}/{name}")
                    ?? throw new InvalidOperationException($"The playbook {name} is not in the server.");
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"{Root}/{name}") { DataStream = content, Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
            }
        }

        return output.ToArray();
    }

    public static string For(string stepName) => stepName switch
    {
        StepNames.Preview or StepNames.AuthorizeKey => Authorize,
        StepNames.VerifyKey or StepNames.VerifyRemoval => Verify,
        StepNames.RemoveKey => Remove,
        _ => throw new InvalidOperationException($"The step '{stepName}' runs no playbook."),
    };
}
