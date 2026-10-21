using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.IO.Net.Http;
using PCL.Core.Minecraft.IdentityModel;
using PCL.Core.Minecraft.Profile.Authentication.Utils.Models;
using PCL.Core.Minecraft.Profile.Models;

namespace PCL.Core.Minecraft.Profile.Authentication.Utils;

public static class MojangUtils
{
    #region "API 定义"

    private const string MojangAuthenticateEndpoint = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string LicenseEndpoint = "https://api.minecraftservices.com/entitlements/mcstore";
    private const string ProfileEndpont = "https://api.minecraftservices.com/minecraft/profile";

    #endregion

    /// <summary>
    /// 发送一次登录请求
    /// </summary>
    /// <param name="xstsToken">XSTS 令牌</param>
    /// <param name="userHash">Xbox User Hash</param>
    /// <returns><see cref="MojangResponse"/></returns>
    public static async Task<MojangResponse?> AuthenticateAsync(string xstsToken, string userHash, CancellationToken token)
    {
        using var response = await HttpRequest.CreatePost(MojangAuthenticateEndpoint)
            .WithContent(new StringContent(
                JsonSerializer.Serialize(new MojangIdentityToken
                {
                    IdentityToken = $"XBL3.0 x={userHash};{xstsToken}"
                }
                ), Encoding.UTF8, "application/json")
            ).SendAsync(cancellationToken: token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new IdentityModelAuthenticationException("minecraft_login_forbidden",
                "Minecraft 登录接口 authentication/login_with_xbox 返回 HTTP 403。" +
                "微软及 Xbox 验证已完成，但 Minecraft 服务拒绝了令牌交换。" +
                "请先确认当前 Client ID 已获准访问 Minecraft Java 服务 API；" +
                "若已获批准，请检查网络代理或网关是否拦截该接口。" +
                "这一步尚未检查账号的游戏购买许可，不能据此判断账号未购买游戏。");
        response.EnsureSuccessStatusCode();
        return await response.AsJsonAsync<MojangResponse>(cancellationToken: token).ConfigureAwait(false);
    }

    /// <summary>
    /// 检查玩家是否持有许可
    /// </summary>
    /// <param name="profile"><see cref="SafeProfile"/></param>
    /// <returns>如果没有有效许可，返回 false</returns>
    public static async Task<bool> CheckLicenseAsync(SafeProfile profile, CancellationToken token)
    {
        using var response = await HttpRequest.Create(LicenseEndpoint)
            .WithAuthentication(profile.TokenType, profile.AccessToken)
            .SendAsync(cancellationToken: token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.AsJsonAsync<JsonObject>(cancellationToken: token).ConfigureAwait(false);
        return result?["items"] is JsonArray items && items.Any(item =>
            item?["name"]?.ToString() is "product_minecraft" or "game_minecraft");
    }
    /// <summary>
    /// 获取玩家的完整档案
    /// </summary>
    /// <returns>档案信息</returns>
    public static async Task<MojangProfile> GetProfileAsync(SafeProfile profile, CancellationToken token)
    {
        using var response = await HttpRequest.Create(ProfileEndpont)
            .WithAuthentication(profile.TokenType, profile.AccessToken)
            .SendAsync(cancellationToken: token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new IdentityModelAuthenticationException("profile_not_created",
                "The Microsoft account has not created a Minecraft Java profile yet.");
        response.EnsureSuccessStatusCode();
        return await response.AsJsonAsync<MojangProfile>(cancellationToken: token).ConfigureAwait(false)
               ?? throw new InvalidOperationException("Minecraft profile response was empty.");
    }
}
