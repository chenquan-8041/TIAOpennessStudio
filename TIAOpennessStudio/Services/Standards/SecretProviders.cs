using System;
using System.Text;

namespace TIAOpennessStudio.Services.Standards
{
    /// <summary>No secret: plaintext library that uses the public fallback salt.</summary>
    public sealed class NullSecretProvider : IStandardsSecretProvider
    {
        public byte[] GetSecretSalt() => Array.Empty<byte>();
    }

    /// <summary>User passphrase as the key source (user-private library).</summary>
    public sealed class PassphraseSecretProvider : IStandardsSecretProvider
    {
        private readonly string _passphrase;
        public PassphraseSecretProvider(string passphrase)
        {
            _passphrase = passphrase ?? string.Empty;
        }
        public byte[] GetSecretSalt() => Encoding.UTF8.GetBytes("user-pp::" + _passphrase);
    }

    /// <summary>
    /// Built-in default key: transparently decrypts the shipped official standards.kb (no passphrase,
    /// no network, no machine binding). Security rests solely on the embedded key; obfuscate/obscure
    /// the assembly before distribution.
    /// </summary>
    public sealed class OfficialSecretProvider : IStandardsSecretProvider
    {
        // built-in default salt (split into segments only to raise the bar for static scanning); pack and decrypt must match.
        private static readonly byte[] P1 = { 0x54, 0x49, 0x41, 0x2D, 0x4F, 0x66, 0x66 };       // "TIA-Off"
        private static readonly byte[] P2 = { 0x69, 0x63, 0x69, 0x61, 0x6C, 0x2D, 0x4B };       // "icial-K"
        private static readonly byte[] P3 = { 0x42, 0x2D, 0x44, 0x65, 0x66, 0x61, 0x75 };       // "B-Defau"
        private static readonly byte[] P4 = { 0x6C, 0x74, 0x2D, 0x32, 0x30, 0x32, 0x36 };       // "lt-2026"

        public byte[] GetSecretSalt()
        {
            var salt = new byte[P1.Length + P2.Length + P3.Length + P4.Length];
            int o = 0;
            Buffer.BlockCopy(P1, 0, salt, o, P1.Length); o += P1.Length;
            Buffer.BlockCopy(P2, 0, salt, o, P2.Length); o += P2.Length;
            Buffer.BlockCopy(P3, 0, salt, o, P3.Length); o += P3.Length;
            Buffer.BlockCopy(P4, 0, salt, o, P4.Length);
            return salt;
        }
    }
}
