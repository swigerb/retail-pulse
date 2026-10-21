namespace RetailPulse.Api.OpenAI.Gateway;

/// <summary>
/// Whether the configured OpenAI endpoint is an APIM AI Gateway. Only <see cref="Apim"/> makes
/// <c>x-rp-gw-*</c> response headers trustworthy; any other value reports calls as bypassed.
/// </summary>
public enum GatewayMode
{
    Direct = 0,
    Apim,
}

public static class GatewayModeSettings
{
    public const string ConfigKey = "OpenAI:GatewayMode";

    /// <summary>Unset, unknown, or direct values never trust gateway headers.</summary>
    public static GatewayMode Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Enum.TryParse(configuration[ConfigKey]?.Trim(), ignoreCase: true, out GatewayMode mode)
            && Enum.IsDefined(mode)
            ? mode
            : GatewayMode.Direct;
    }
}
