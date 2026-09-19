using System.Security.Cryptography;
using System.Text;

namespace Accounts.Application;

public static class RegistrationSessionReference
{
    public static (string RawReference, byte[] Hash) Create()
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return (raw, SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}
