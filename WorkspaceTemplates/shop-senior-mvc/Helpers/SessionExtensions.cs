using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;

namespace shop_senior_mvc.Helpers
{
    public static class SessionExtensions
    {
        public static void SetObjectAsJson(this ISession session, string key, object value)
        {
            session.SetString(key, JsonConvert.SerializeObject(value));
        }

        public static T? GetObjectFromJson<T>(this ISession session, string key)
        {
            var value = session.GetString(key);
            return value == null ? default : JsonConvert.DeserializeObject<T>(value);
        }

        public static bool IsUserLoggedIn(this ISession session)
        {
            return !string.IsNullOrEmpty(session.GetString("UserId")) || 
                   !string.IsNullOrEmpty(session.GetString("user_id")) ||
                   !string.IsNullOrEmpty(session.GetString("UserEmail"));
        }

        public static string? GetUserId(this ISession session)
        {
            return session.GetString("UserId") ?? session.GetString("user_id");
        }

        public static string? GetUsername(this ISession session)
        {
            return session.GetString("Username") ?? session.GetString("username") ?? "Khách hàng";
        }

        public static void SetUserLoggedIn(this ISession session, string userId, string username)
        {
            session.SetString("UserId", userId);
            session.SetString("user_id", userId);
            session.SetString("Username", username);
            session.SetString("username", username);
        }
    }
}
