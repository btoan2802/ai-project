using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace BtoanAiNet.Helpers
{
    public static class AuthUserHelper
    {
        public static int GetCurrentUserId(HttpContext? context)
        {
            if (context == null) return 1;

            var claimId = context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (int.TryParse(claimId, out int uid) && uid > 0) return uid;

            var sessionUid = context.Session?.GetInt32("user_id");
            if (sessionUid.HasValue && sessionUid.Value > 0) return sessionUid.Value;

            if (context.Request.Headers.TryGetValue("X-User-Id", out var headerUid)
                && int.TryParse(headerUid, out int hUid) && hUid > 0)
            {
                return hUid;
            }

            if (context.Request.Query.TryGetValue("user_id", out var qUid)
                && int.TryParse(qUid, out int parsedQUid) && parsedQUid > 0)
            {
                return parsedQUid;
            }

            if (context.Request.Cookies.TryGetValue("user_id", out var cookieUid)
                && int.TryParse(cookieUid, out int cUid) && cUid > 0)
            {
                return cUid;
            }

            return 1;
        }
    }
}
