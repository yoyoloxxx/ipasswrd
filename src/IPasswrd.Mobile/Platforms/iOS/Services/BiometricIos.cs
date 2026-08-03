using Foundation;
using IPasswrd.Mobile.Services;
using LocalAuthentication;

namespace IPasswrd.Mobile.Platforms.iOS.Services;

/// <summary>Face ID / Touch ID (с откатом на код устройства).</summary>
public sealed class BiometricIos : IBiometricAuth
{
    public bool IsAvailable
    {
        get
        {
            using var ctx = new LAContext();
            return ctx.CanEvaluatePolicy(LAPolicy.DeviceOwnerAuthentication, out NSError _);
        }
    }

    public string Kind
    {
        get
        {
            using var ctx = new LAContext();
            if (!ctx.CanEvaluatePolicy(LAPolicy.DeviceOwnerAuthenticationWithBiometrics, out NSError _))
                return "Код устройства";
            return ctx.BiometryType switch
            {
                LABiometryType.FaceId => "Face ID",
                LABiometryType.TouchId => "Touch ID",
                _ => "Биометрия",
            };
        }
    }

    public async Task<bool> AuthenticateAsync(string reason)
    {
        try
        {
            using var ctx = new LAContext { LocalizedCancelTitle = "Отмена" };
            if (!ctx.CanEvaluatePolicy(LAPolicy.DeviceOwnerAuthentication, out NSError _))
                return false;
            (bool ok, NSError? _) = await ctx.EvaluatePolicyAsync(LAPolicy.DeviceOwnerAuthentication, reason);
            return ok;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
