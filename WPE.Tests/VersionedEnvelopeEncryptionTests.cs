using System.Security.Cryptography;
using System.Text;
using \u5E01\u5B89\u91CF\u5316\u673A\u5668\u4EBA.Services.Security;

namespace WPE.Tests;

public sealed class VersionedEnvelopeEncryptionTests
{
    private static readonly EnvelopeAssociatedData Aad = new("runtime-audit", "record-17", 4);

    [Fact]
    public void RoundTrip_UsesRandomNonceAndSeparateAuthenticationTag()
    {
        using var protector = new TemporaryAesKeyProtector();
        var service = new VersionedEnvelopeEncryptionService(protector);
        var plaintext = Encoding.UTF8.GetBytes("synthetic-test-value");

        var first = service.Encrypt(plaintext, Aad);
        var second = service.Encrypt(plaintext, Aad);

        Assert.Equal(VersionedEnvelopeEncryptionService.CurrentVersion, first.Version);
        Assert.Equal(VersionedEnvelopeEncryptionService.NonceLength, first.Nonce.Length);
        Assert.Equal(VersionedEnvelopeEncryptionService.AuthenticationTagLength, first.AuthenticationTag.Length);
        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.Equal(plaintext, service.Decrypt(first, Aad));
    }

    [Fact]
    public void Decrypt_RejectsUnknownVersionBeforeUsingKeyStore()
    {
        using var protector = new TemporaryAesKeyProtector();
        var service = new VersionedEnvelopeEncryptionService(protector);
        var envelope = service.Encrypt([1, 2, 3], Aad) with { Version = 99 };
        protector.ResetUnwrapCount();

        var error = Assert.Throws<EnvelopeEncryptionException>(() => service.Decrypt(envelope, Aad));

        Assert.Equal("envelope.version-unsupported", error.ReasonCode);
        Assert.Equal(0, protector.UnwrapCount);
    }

    [Theory]
    [InlineData("ciphertext")]
    [InlineData("tag")]
    [InlineData("wrapped-dek")]
    public void Decrypt_RejectsTampering(string target)
    {
        using var protector = new TemporaryAesKeyProtector();
        var service = new VersionedEnvelopeEncryptionService(protector);
        var envelope = service.Encrypt([7, 8, 9], Aad);
        envelope = target switch
        {
            "ciphertext" => envelope with { Ciphertext = FlipFirst(envelope.Ciphertext) },
            "tag" => envelope with { AuthenticationTag = FlipFirst(envelope.AuthenticationTag) },
            _ => envelope with { WrappedDek = FlipFirst(envelope.WrappedDek) }
        };

        var error = Assert.Throws<EnvelopeEncryptionException>(() => service.Decrypt(envelope, Aad));

        Assert.Contains(error.ReasonCode, new[]
        {
            "envelope.authentication-failed",
            "envelope.key-unprotect-failed"
        });
    }

    [Fact]
    public void Decrypt_RejectsAssociatedDataMismatch()
    {
        using var protector = new TemporaryAesKeyProtector();
        var service = new VersionedEnvelopeEncryptionService(protector);
        var envelope = service.Encrypt([1, 3, 5], Aad);

        var error = Assert.Throws<EnvelopeEncryptionException>(() =>
            service.Decrypt(envelope, Aad with { RecordId = "record-18" }));

        Assert.Equal("envelope.key-unprotect-failed", error.ReasonCode);
    }

    [Theory]
    [InlineData(true, false, "envelope.nonce-length-invalid")]
    [InlineData(false, true, "envelope.tag-length-invalid")]
    public void Decrypt_RejectsInvalidNonceOrTagLength(bool invalidNonce, bool invalidTag, string reasonCode)
    {
        using var protector = new TemporaryAesKeyProtector();
        var service = new VersionedEnvelopeEncryptionService(protector);
        var envelope = service.Encrypt([2, 4, 6], Aad) with
        {
            Nonce = invalidNonce ? new byte[11] : new byte[12],
            AuthenticationTag = invalidTag ? new byte[15] : new byte[16]
        };

        var error = Assert.Throws<EnvelopeEncryptionException>(() => service.Decrypt(envelope, Aad));

        Assert.Equal(reasonCode, error.ReasonCode);
    }

    [Fact]
    public void EncryptAndDecrypt_RejectUnavailableKeyStore()
    {
        using var protector = new TemporaryAesKeyProtector();
        var service = new VersionedEnvelopeEncryptionService(protector);
        var envelope = service.Encrypt([1], Aad);
        protector.IsAvailable = false;

        Assert.Equal("envelope.keystore-unavailable", Assert.Throws<EnvelopeEncryptionException>(
            () => service.Encrypt([1], Aad)).ReasonCode);
        Assert.Equal("envelope.keystore-unavailable", Assert.Throws<EnvelopeEncryptionException>(
            () => service.Decrypt(envelope, Aad)).ReasonCode);
    }

    [Fact]
    public void Decrypt_ClearsProtectorOwnedPlaintextDekAfterUse()
    {
        using var protector = new TemporaryAesKeyProtector();
        var service = new VersionedEnvelopeEncryptionService(protector);
        var envelope = service.Encrypt([4, 5, 6], Aad);

        _ = service.Decrypt(envelope, Aad);

        Assert.NotNull(protector.LastReturnedDek);
        Assert.All(protector.LastReturnedDek!, value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(true,"envelope.key-wrap-failed")]
    [InlineData(false,"envelope.key-unprotect-failed")]
    public void ProtectorFailures_DoNotRetainOrRenderSensitiveInnerException(bool wrap,string reasonCode)
    {
        const string sensitive="secret=top-secret; path=C:\\private\\keys\\master.key; key-id=customer-key-42";
        using var protector=new ThrowingKeyProtector(sensitive,wrap);
        var service=new VersionedEnvelopeEncryptionService(protector);

        EnvelopeEncryptionException error;
        if(wrap)
            error=Assert.Throws<EnvelopeEncryptionException>(()=>service.Encrypt([1,2,3],Aad));
        else
        {
            using var working=new TemporaryAesKeyProtector();
            var envelope=new VersionedEnvelopeEncryptionService(working).Encrypt([1,2,3],Aad) with{KeyId=protector.KeyId};
            error=Assert.Throws<EnvelopeEncryptionException>(()=>service.Decrypt(envelope,Aad));
        }

        Assert.Equal(reasonCode,error.ReasonCode);
        Assert.Null(error.InnerException);
        Assert.Equal("Envelope encryption operation was rejected.",error.Message);
        Assert.DoesNotContain("top-secret",error.ToString(),StringComparison.Ordinal);
        Assert.DoesNotContain("private",error.ToString(),StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("customer-key-42",error.ToString(),StringComparison.Ordinal);
    }

    private static byte[] FlipFirst(byte[] value)
    {
        var changed = value.ToArray();
        changed[0] ^= 0x80;
        return changed;
    }

    private sealed class TemporaryAesKeyProtector : IPlatformKeyProtector, IDisposable
    {
        private readonly byte[] _kek = RandomNumberGenerator.GetBytes(32);

        public bool IsAvailable { get; set; } = true;
        public string KeyId => "temporary-test-kek";
        public int UnwrapCount { get; private set; }
        public byte[]? LastReturnedDek { get; private set; }

        public byte[] WrapKey(ReadOnlySpan<byte> dek, EnvelopeKeyContext context)
        {
            EnsureAvailable();
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var ciphertext = new byte[dek.Length];
            using var cipher = new AesGcm(_kek, 16);
            cipher.Encrypt(nonce, dek, ciphertext, tag, context.AssociatedDataHash);
            return [.. nonce, .. tag, .. ciphertext];
        }

        public byte[] UnwrapKey(ReadOnlySpan<byte> wrappedDek, EnvelopeKeyContext context)
        {
            EnsureAvailable();
            UnwrapCount++;
            if (wrappedDek.Length != 60)
                throw new CryptographicException("Invalid wrapped key.");

            var plaintextDek = new byte[32];
            try
            {
                using var cipher = new AesGcm(_kek, 16);
                cipher.Decrypt(
                    wrappedDek[..12],
                    wrappedDek[28..],
                    wrappedDek[12..28],
                    plaintextDek,
                    context.AssociatedDataHash);
                LastReturnedDek = plaintextDek;
                return plaintextDek;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(plaintextDek);
                throw;
            }
        }

        public void ResetUnwrapCount() => UnwrapCount = 0;

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(_kek);
            if (LastReturnedDek is not null)
                CryptographicOperations.ZeroMemory(LastReturnedDek);
        }

        private void EnsureAvailable()
        {
            if (!IsAvailable)
                throw new CryptographicException("Temporary keystore unavailable.");
        }
    }

    private sealed class ThrowingKeyProtector(string message,bool throwOnWrap):IPlatformKeyProtector,IDisposable
    {
        public bool IsAvailable=>true;
        public string KeyId=>"safe-test-key";
        public byte[] WrapKey(ReadOnlySpan<byte> dek,EnvelopeKeyContext context)=>throwOnWrap
            ?throw new EnvelopeEncryptionException("injected-"+message,new InvalidOperationException(message))
            :dek.ToArray();
        public byte[] UnwrapKey(ReadOnlySpan<byte> wrappedDek,EnvelopeKeyContext context)=>!throwOnWrap
            ?throw new CryptographicException(message)
            :wrappedDek.ToArray();
        public void Dispose(){}
    }
}
