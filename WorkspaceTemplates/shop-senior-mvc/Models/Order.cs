#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace shop_senior_mvc.Models
{
    public class Order
    {
        public int Id { get; set; }

        [Required, MaxLength(100)]
        public string CustomerName { get; set; } = string.Empty;

        public string CustomerEmail { get; set; } = string.Empty;

        [Required, Phone]
        public string Phone { get; set; } = string.Empty;

        [NotMapped]
        public string CustomerPhone { get => Phone; set => Phone = value; }

        [Required, MaxLength(250)]
        public string Address { get; set; } = string.Empty;

        [NotMapped]
        public string ShippingAddress { get => Address; set => Address = value; }

        public string Note { get; set; } = string.Empty;

        public string Status { get; set; } = "Chờ xác nhận";

        [Column(TypeName = "decimal(18,2)")]
        public decimal TotalAmount { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [NotMapped]
        public DateTime OrderDate { get => CreatedAt; set => CreatedAt = value; }

        public List<OrderItem> Items { get; set; } = new List<OrderItem>();

        [NotMapped]
        public List<OrderItem> OrderItems { get => Items; set => Items = value; }
    }

    public class OrderItem
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public Order? Order { get; set; }

        public int ProductId { get; set; }
        public Product? Product { get; set; }

        public int Quantity { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal UnitPrice { get; set; }
    }
}
