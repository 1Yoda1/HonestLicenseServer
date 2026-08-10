namespace HonestLicenseServer.Authentication;

public static class OpaqueBearerDefaults
{
    public const string Scheme = "OpaqueBearer";
    public const string ActiveClientPolicy = "ActiveClient";
    public const string ActiveDevicePolicy = "ActiveDevice";
}

public static class HonestClaimTypes
{
    public const string ClientId = "honest:client_id";
    public const string ExternalClientId = "honest:external_client_id";
    public const string ClientActive = "honest:client_active";
    public const string DeviceId = "honest:device_id";
    public const string ExternalDeviceId = "honest:external_device_id";
    public const string DeviceStatus = "honest:device_status";
    public const string SessionId = "honest:session_id";
}
