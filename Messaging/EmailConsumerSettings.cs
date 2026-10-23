namespace Mango.Services.EmailAPI.Messaging;

public sealed class EmailConsumerSettings
{
    public string ConnectionString { get; set; } = string.Empty;

    public string EmailShoppingCartQueue { get; set; } = string.Empty;

    public string RegisterUserQueue { get; set; } = string.Empty;

    public string OrderCreatedEmailQueue { get; set; } = string.Empty;
}