using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace \u5E01\u5B89\u91CF\u5316\u673A\u5668\u4EBA.Services.Security;

public sealed record EnvelopeAssociatedData(string RecordType, string RecordId, int RecordVersion);

public sealed record EnvelopeKeyContext(int EnvelopeVersion, byte[] AssociatedDataHash);

public sealed record EncryptedEnvelope(
    int Version,
    string Algorithm,
    string KeyId,
    byte[] WrappedDek,
    byte[] Nonce,
    byte[] AuthenticationTag,
    byte[] Ciphertext);

public interface IPlatformKeyProtector
{
    bool IsAvailable { get; }

    string KeyId { get; }

    byte[] WrapKey(ReadOnlySpan<byte> dek, EnvelopeKeyContext context);

    byte[] UnwrapKey(ReadOnlySpan<byte> wrappedDek, EnvelopeKeyContext context);
}

public sealed class EnvelopeEncryptionException : CryptographicException
{
    public EnvelopeEncryptionException(string reasonCode)
        : base("Envelope encryption operation was rejected.")
    {
        ReasonCode = reasonCode;
    }

    public EnvelopeEncryptionException(string reasonCode, Exception innerException)
        : base("Envelope encryption operation was rejected.")
    {
        _ = innerException;
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

public sealed class VersionedEnvelopeEncryptionService
{
    public const int CurrentVersion = 1;
    public const string CurrentAlgorithm = "AES-256-GCM";
    public const int DekLength = 32;
    public const int NonceLength = 12;
    public const int AuthenticationTagLength = 16;

    private readonly IPlatformKeyProtector _keyProtector;

    public VersionedEnvelopeEncryptionService(IPlatformKeyProtector keyProtector)
        => _keyProtector = keyProtector ?? throw new ArgumentNullException(nameof(keyProtector));

    public EncryptedEnvelope Encrypt(ReadOnlySpan<byte> plaintext, EnvelopeAssociatedData associatedData)
    {
        EnsureKeyStoreAvailable();
        var aad = EncodeAssociatedData(associatedData);
        var keyContext = CreateKeyContext(aad);
        var dek = RandomNumberGenerator.GetBytes(DekLength);

        try
        {
            byte[] wrappedDek;
            try
            {
                wrappedDek = _keyProtector.WrapKey(dek, keyContext);
            }
            catch (Exception exception)
            {
                throw new EnvelopeEncryptionException("envelope.key-wrap-failed", exception);
            }

            if (wrappedDek is null || wrappedDek.Length == 0)
                throw new EnvelopeEncryptionException("envelope.wrapped-dek-invalid");

            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var tag = new byte[AuthenticationTagLength];
            var ciphertext = new byte[plaintext.Length];
            using var cipher = new AesGcm(dek, AuthenticationTagLength);
            cipher.Encrypt(nonce, plaintext, ciphertext, tag, aad);

            return new(
                CurrentVersion,
                CurrentAlgorithm,
                _keyProtector.KeyId,
                wrappedDek,
                nonce,
                tag,
                ciphertext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public byte[] Decrypt(EncryptedEnvelope envelope, EnvelopeAssociatedData associatedData)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateEnvelope(envelope);
        EnsureKeyStoreAvailable();

        if (!string.Equals(envelope.KeyId, _keyProtector.KeyId, StringComparison.Ordinal))
            throw new EnvelopeEncryptionException("envelope.key-id-mismatch");

        var aad = EncodeAssociatedData(associatedData);
        var keyContext = CreateKeyContext(aad);
        byte[]? dek = null;

        try
        {
            try
            {
                dek = _keyProtector.UnwrapKey(envelope.WrappedDek, keyContext);
            }
            catch (Exception exception)
            {
                throw new EnvelopeEncryptionException("envelope.key-unprotect-failed", exception);
            }

            if (dek is null || dek.Length != DekLength)
                throw new EnvelopeEncryptionException("envelope.dek-length-invalid");

            var plaintext = new byte[envelope.Ciphertext.Length];
            try
            {
                using var cipher = new AesGcm(dek, AuthenticationTagLength);
                cipher.Decrypt(
                    envelope.Nonce,
                    envelope.Ciphertext,
                    envelope.AuthenticationTag,
                    plaintext,
                    aad);
                return plaintext;
            }
            catch (CryptographicException exception)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw new EnvelopeEncryptionException("envelope.authentication-failed", exception);
            }
        }
        finally
        {
            if (dek is not null)
                CryptographicOperations.ZeroMemory(dek);
        }
    }

    private void EnsureKeyStoreAvailable()
    {
        if (!_keyProtector.IsAvailable)
            throw new EnvelopeEncryptionException("envelope.keystore-unavailable");
        if (string.IsNullOrWhiteSpace(_keyProtector.KeyId))
            throw new EnvelopeEncryptionException("envelope.key-id-missing");
    }

    private static void ValidateEnvelope(EncryptedEnvelope envelope)
    {
        if (envelope.Version != CurrentVersion)
            throw new EnvelopeEncryptionException("envelope.version-unsupported");
        if (!string.Equals(envelope.Algorithm, CurrentAlgorithm, StringComparison.Ordinal))
            throw new EnvelopeEncryptionException("envelope.algorithm-unsupported");
        if (envelope.Nonce is null || envelope.Nonce.Length != NonceLength)
            throw new EnvelopeEncryptionException("envelope.nonce-length-invalid");
        if (envelope.AuthenticationTag is null || envelope.AuthenticationTag.Length != AuthenticationTagLength)
            throw new EnvelopeEncryptionException("envelope.tag-length-invalid");
        if (envelope.WrappedDek is null || envelope.WrappedDek.Length == 0)
            throw new EnvelopeEncryptionException("envelope.wrapped-dek-invalid");
        if (envelope.Ciphertext is null)
            throw new EnvelopeEncryptionException("envelope.ciphertext-missing");
    }

    private static EnvelopeKeyContext CreateKeyContext(byte[] aad)
        => new(CurrentVersion, SHA256.HashData(aad));

    private static byte[] EncodeAssociatedData(EnvelopeAssociatedData associatedData)
    {
        ArgumentNullException.ThrowIfNull(associatedData);
        if (string.IsNullOrWhiteSpace(associatedData.RecordType))
            throw new EnvelopeEncryptionException("envelope.record-type-missing");
        if (string.IsNullOrWhiteSpace(associatedData.RecordId))
            throw new EnvelopeEncryptionException("envelope.record-id-missing");
        if (associatedData.RecordVersion <= 0)
            throw new EnvelopeEncryptionException("envelope.record-version-invalid");

        var type = Encoding.UTF8.GetBytes(associatedData.RecordType);
        var id = Encoding.UTF8.GetBytes(associatedData.RecordId);
        var encoded = new byte[sizeof(int) + type.Length + sizeof(int) + id.Length + sizeof(int)];
        var offset = 0;
        BinaryPrimitives.WriteInt32BigEndian(encoded.AsSpan(offset), type.Length);
        offset += sizeof(int);
        type.CopyTo(encoded, offset);
        offset += type.Length;
        BinaryPrimitives.WriteInt32BigEndian(encoded.AsSpan(offset), id.Length);
        offset += sizeof(int);
        id.CopyTo(encoded, offset);
        offset += id.Length;
        BinaryPrimitives.WriteInt32BigEndian(encoded.AsSpan(offset), associatedData.RecordVersion);
        return encoded;
    }
}
