using System.ComponentModel.DataAnnotations;

namespace shop_senior_mvc.Models.ViewModels
{
    public class CheckoutViewModel
    {
        [Required(ErrorMessage = "Vui lòng nhập họ tên")]
        [Display(Name = "Họ tên người nhận")]
        public string CustomerName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Vui lòng nhập số điện thoại")]
        [Phone(ErrorMessage = "Số điện thoại không hợp lệ")]
        [Display(Name = "Số điện thoại")]
        public string Phone { get; set; } = string.Empty;

        [Required(ErrorMessage = "Vui lòng nhập địa chỉ giao hàng")]
        [Display(Name = "Địa chỉ nhận hàng")]
        public string Address { get; set; } = string.Empty;

        [Display(Name = "Ghi chú đơn hàng")]
        public string Note { get; set; } = string.Empty;
    }

    public class HomeViewModel
    {
        public List<shop_senior_mvc.Models.Product> FeaturedProducts { get; set; } = new List<shop_senior_mvc.Models.Product>();
        public List<shop_senior_mvc.Models.Category> Categories { get; set; } = new List<shop_senior_mvc.Models.Category>();
    }
}
