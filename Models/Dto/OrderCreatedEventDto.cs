using Newtonsoft.Json;

namespace Mango.Services.EmailAPI.Models.Dto
{
    public class OrderCreatedEventDto
    {
        [JsonProperty("eventId")]
        public Guid EventId { get; set; }

        [JsonProperty("eventType")]
        public string EventType { get; set; } = string.Empty;

        [JsonProperty("contractVersion")]
        public int ContractVersion { get; set; }

        [JsonProperty("occurredAtUtc")]
        public DateTime OccurredAtUtc { get; set; }

        [JsonProperty("orderId")]
        public int OrderId { get; set; }

        [JsonProperty("userId")]
        public string UserId { get; set; } = string.Empty;

        [JsonProperty("customerEmail")]
        public string CustomerEmail { get; set; } = string.Empty;

        [JsonProperty("totalAmount")]
        public double TotalAmount { get; set; }

        [JsonProperty("currency")]
        public string Currency { get; set; } = "INR";

        [JsonProperty("items")]
        public List<OrderCreatedItemDto> Items { get; set; } = new();
    }

    public class OrderCreatedItemDto
    {
        [JsonProperty("productId")]
        public int ProductId { get; set; }

        [JsonProperty("productName")]
        public string ProductName { get; set; } = string.Empty;

        [JsonProperty("quantity")]
        public int Quantity { get; set; }

        [JsonProperty("unitPrice")]
        public double UnitPrice { get; set; }
    }
}
