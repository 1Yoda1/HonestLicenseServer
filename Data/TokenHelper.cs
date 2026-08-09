using System.Security.Cryptography;
using System.Text;

namespace HonestLicenseServer.Data;

public static class TokenHelper
{
    public static string Create(int bytes = 48) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes));
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
