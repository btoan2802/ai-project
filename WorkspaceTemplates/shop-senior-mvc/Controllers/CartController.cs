using Microsoft.AspNetCore.Mvc;
using shop_senior_mvc.Data;
using shop_senior_mvc.Models.ViewModels;
using shop_senior_mvc.Services;

namespace shop_senior_mvc.Controllers
{
    public class CartController : Controller
    {
        private readonly CartService _cartService;
        private readonly AppDbContext _context;

        public CartController(CartService cartService, AppDbContext context)
        {
            _cartService = cartService;
            _context = context;
        }

        public IActionResult Index()
        {
            var vm = new CartViewModel
            {
                Items = _cartService.GetCartItems()
            };
            return View(vm);
        }

        [HttpPost]
        public IActionResult Add(int productId, int quantity = 1)
        {
            var product = _context.Products.Find(productId);
            if (product != null)
            {
                _cartService.AddToCart(product, quantity);
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public IActionResult Update(int productId, int quantity)
        {
            _cartService.UpdateQuantity(productId, quantity);
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public IActionResult Remove(int productId)
        {
            _cartService.RemoveFromCart(productId);
            return RedirectToAction(nameof(Index));
        }
    }
}
