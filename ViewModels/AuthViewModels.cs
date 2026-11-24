namespace BtoanAiNet.ViewModels
{
    public class LoginRequestModel
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
    }

    public class RegisterRequestModel
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
    }

    public class GoogleLoginRequestModel
    {
        public string? Token { get; set; }
    }

    public class UpdateNameRequestModel
    {
        public int? User_Id { get; set; }
        public string? Full_Name { get; set; }
        public string? Dob { get; set; }
    }
}
