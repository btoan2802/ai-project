using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using shop_senior_mvc.Helpers;
using shop_senior_mvc.Models;

namespace shop_senior_mvc.Services
{
    public class CartService
    {
        private readonly ISession _session;
        private const string CartSessionKey = "SeniorShop_Cart";

        public CartService(IHttpContextAccessor httpContextAccessor)
        {
            _session = httpContextAccessor.HttpContext?.Session;
        }

        public List<CartItem> GetCartItems()
        {
            if (_session == null) return new List<CartItem>();
            return _session.GetObjectFromJson<List<CartItem>>(CartSessionKey) ?? new List<CartItem>();
        }

        public void AddToCart(Product product, int quantity = 1)
        {
            if (_session == null || product == null) return;
            var cart = GetCartItems();
            var existing = cart.FirstOrDefault(x => x.ProductId == product.Id);
            if (existing != null)
            {
                existing.Quantity += quantity;
            }
            else
            {
                cart.Add(new CartItem
                {
                    ProductId = product.Id,
                    ProductName = product.Name,
                    Price = product.Price,
                    ImageUrl = product.ImageUrl,
                    Quantity = quantity
                });
            }
            _session.SetObjectAsJson(CartSessionKey, cart);
        }

        public void UpdateQuantity(int productId, int quantity)
        {
            if (_session == null) return;
            var cart = GetCartItems();
            var item = cart.FirstOrDefault(x => x.ProductId == productId);
            if (item != null)
            {
                if (quantity <= 0) cart.Remove(item);
                else item.Quantity = quantity;
                _session.SetObjectAsJson(CartSessionKey, cart);
            }
        }

        public void RemoveFromCart(int productId)
        {
            if (_session == null) return;
            var cart = GetCartItems();
            var item = cart.FirstOrDefault(x => x.ProductId == productId);
            if (item != null)
            {
                cart.Remove(item);
                _session.SetObjectAsJson(CartSessionKey, cart);
            }
        }

        public void ClearCart()
        {
            _session?.Remove(CartSessionKey);
        }

        public decimal GetTotal() => GetCartItems().Sum(x => x.Total);
        public int GetTotalItems() => GetCartItems().Sum(x => x.Quantity);
    }
}
