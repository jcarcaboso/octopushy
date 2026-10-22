using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Server.Data;
using Octopushy.Server.Store;

namespace Octopushy.Tests.Server;

public class EnvelopeTests
{
    [Fact]
    public void A_sealed_value_opens_only_in_its_own_context()
    {
        var envelope = new Envelope(new KeyEncryptionKey(RandomNumberGenerator.GetBytes(32)));
        var (ciphertext, wrapped) = envelope.Seal("hunter2"u8, "store\0a\01");
        Assert.Equal("hunter2"u8.ToArray(), envelope.Open(ciphertext, wrapped, "store\0a\01"));
        Assert.ThrowsAny<CryptographicException>(() => envelope.Open(ciphertext, wrapped, "store\0a\02"));
        Assert.ThrowsAny<CryptographicException>(() => envelope.Open(ciphertext, wrapped, "store\0b\01"));
    }

    [Fact]
    public void A_different_key_encryption_key_cannot_open_it()
    {
        var blob = new Envelope(new KeyEncryptionKey(RandomNumberGenerator.GetBytes(32))).SealBlob("plan"u8, "artifact");
        Assert.ThrowsAny<CryptographicException>(() => new Envelope(new KeyEncryptionKey(RandomNumberGenerator.GetBytes(32))).OpenBlob(blob, "artifact"));
    }
}

[Collection(PostgresCollection.Name)]
public class VariableStoreTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Values_are_versioned_encrypted_and_secrets_stay_secret()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        await using var scope = server.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<VariableStore>();
        await store.SetAsync("dns/zone", "hlab.example.com", Visibilities.Plain, "The zone", CancellationToken.None);
        await store.SetAsync("dns/token", "first", Visibilities.Secret, null, CancellationToken.None);
        var candidate = await store.StageAsync("dns/token", "second", null, CancellationToken.None);
        Assert.Equal(("first", 1), await store.GetAsync("dns/token", CancellationToken.None));
        await store.PromoteAsync("dns/token", candidate, 1, "Checked replacement access externally.", "test", CancellationToken.None);

        Assert.Equal("hlab.example.com", await store.GetPlainAsync("dns/zone", CancellationToken.None));
        Assert.Null(await store.GetPlainAsync("dns/token", CancellationToken.None));
        Assert.Equal(("second", 2), await store.GetAsync("dns/token", CancellationToken.None));
        await Assert.ThrowsAsync<StoreException>(() => store.SetAsync("dns/token", "x", Visibilities.Plain, null, CancellationToken.None));
        await Assert.ThrowsAsync<StoreException>(() => store.DeleteAsync("dns/token", ["home/korriban"], CancellationToken.None));

        var db = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
        var stored = await db.StoreVersions.AsNoTracking().Where(v => v.Key == "dns/token").ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, v => Assert.DoesNotContain("second"u8.ToArray(), v.Ciphertext.Chunk(6).Select(c => c.ToArray())));
    }
}
