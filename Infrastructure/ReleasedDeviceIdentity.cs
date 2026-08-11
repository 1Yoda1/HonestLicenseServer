namespace HonestLicenseServer.Infrastructure;

public static class ReleasedDeviceIdentity
{
    public static string Create(int deviceId, string externalDeviceId) =>
        $"released-{deviceId}-{externalDeviceId}";

    public static bool TryGetOriginal(int deviceId, string storedExternalDeviceId, out string original)
    {
        string prefix = $"released-{deviceId}-";
        if (storedExternalDeviceId.StartsWith(prefix, StringComparison.Ordinal))
        {
            original = storedExternalDeviceId[prefix.Length..];
            return true;
        }

        original = "";
        return false;
    }
}
