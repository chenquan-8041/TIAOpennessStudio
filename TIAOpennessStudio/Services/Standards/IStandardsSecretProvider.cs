namespace TIAOpennessStudio.Services.Standards
{
    /// <summary>Secret-salt provider: returns the salt used for key derivation (an empty array means no secret, falling back to the public salt).</summary>
    public interface IStandardsSecretProvider
    {
        byte[] GetSecretSalt();
    }
}
