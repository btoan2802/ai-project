using System.Collections.Generic;
using System.Linq;

namespace shop_senior_mvc.Models.ViewModels
{
    public class CartViewModel
    {
        public List<CartItem> Items { get; set; } = new List<CartItem>();
        public decimal GrandTotal => Items.Sum(x => x.Total);
    }
}
