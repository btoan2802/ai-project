using System.Linq;
using Microsoft.AspNetCore.Mvc;
using shop_senior_mvc.Data;
using shop_senior_mvc.Models;
using shop_senior_mvc.Models.ViewModels;
using shop_senior_mvc.Services;

namespace shop_senior_mvc.Controllers
{
    public class OrdersController : Controller
    {
        private readonly CartService _cartService;
        private readonly AppDbContext _context;

        public OrdersController(CartService cartService, AppDbContext context)
        {
            _cartService = cartService;
            _context = context;
        }

        public IActionResult Checkout()
        {
            var items = _cartService.GetCartItems();
            if (items.Count == 0) return RedirectToAction("Index", "Cart");

            ViewBag.CartTotal = _cartService.GetTotal();
            ViewBag.CartItems = items;
            return View(new CheckoutViewModel());
        }

        [HttpPost]
        public IActionResult Checkout(CheckoutViewModel model)
        {
            var items = _cartService.GetCartItems();
            if (items.Count == 0) return RedirectToAction("Index", "Cart");

            if (!ModelState.IsValid)
            {
                ViewBag.CartTotal = _cartService.GetTotal();
                ViewBag.CartItems = items;
                return View(model);
            }

            var order = new Order
            {
                CustomerName = model.CustomerName,
                Phone = model.Phone,
                Address = model.Address,
                Note = model.Note ?? "",
                TotalAmount = _cartService.GetTotal(),
                Items = items.Select(i => new OrderItem
                {
                    ProductId = i.ProductId,
                    Quantity = i.Quantity,
                    UnitPrice = i.Price
                }).ToList()
            };

            _context.Orders.Add(order);
            _context.SaveChanges();
            _cartService.ClearCart();

            return RedirectToAction(nameof(Success), new { id = order.Id });
        }

        public IActionResult Success(int id)
        {
            var order = _context.Orders.Find(id);
            if (order == null) return RedirectToAction("Index", "Home");
            return View(order);
        }
    }
}
