using Mango.Services.EmailAPI.Models.Dto;
using Mango.Services.EmailAPI.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;

namespace Mango.Services.EmailAPI.Messaging
{
    public sealed class RabbitMQMessageConsumer : IHostedService
    {
        private readonly string _connectionString;
        private readonly string _emailCartQueue;
        private readonly string _registerUserQueue;
        private readonly string _orderCreatedEmailQueue;
        private readonly EmailService _emailService;

        private IConnection? _connection;
        private IChannel? _cartChannel;
        private IChannel? _registrationChannel;
        private IChannel? _orderCreatedChannel;

        private string? _cartConsumerTag;
        private string? _registrationConsumerTag;
        private string? _orderCreatedConsumerTag;

        public RabbitMQMessageConsumer(
            IOptions<MessageQueueSettings> options,
            IConfiguration configuration,
            EmailService emailService)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(emailService);

            _emailService = emailService;

            _connectionString =
                options.Value.RabbitMQ?.ConnectionString
                ?? throw new InvalidOperationException(
                    "RabbitMQ connection string is missing.");

            _emailCartQueue = GetQueueName(
                configuration, "EmailShoppingCartQueue");

            _registerUserQueue = GetQueueName(
                configuration, "RegisterUserQueue");

            _orderCreatedEmailQueue = GetQueueName(
                configuration, "OrderCreatedEmailQueue");
        }

        private static string GetQueueName(
            IConfiguration configuration, string key)
        {
            return configuration[$"TopicAndQueueNames:{key}"]
                ?? throw new InvalidOperationException(
                    $"Queue configuration '{key}' is missing.");
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var factory = new ConnectionFactory
            {
                Uri = new Uri(_connectionString)
            };

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                _connection = await factory.CreateConnectionAsync();

                // Each consumer owns its own channel.
                _cartChannel = await CreateConsumerChannelAsync(
                    _emailCartQueue);

                _registrationChannel = await CreateConsumerChannelAsync(
                    _registerUserQueue);

                _orderCreatedChannel = await CreateConsumerChannelAsync(
                    _orderCreatedEmailQueue);

                _cartConsumerTag = await StartCartConsumerAsync(
                    _cartChannel);

                _registrationConsumerTag =
                    await StartRegistrationConsumerAsync(
                        _registrationChannel);

                _orderCreatedConsumerTag =
                    await StartOrderCreatedConsumerAsync(
                        _orderCreatedChannel);

                Console.WriteLine(
                    "[EmailAPI] All RabbitMQ consumers started.");
            }
            catch
            {
                await DisposeRabbitMQResourcesAsync();
                throw;
            }
        }

        private async Task<IChannel> CreateConsumerChannelAsync(
            string queueName)
        {
            var channel = await _connection!.CreateChannelAsync();

            try
            {
                await channel.QueueDeclareAsync(
                    queue: queueName,
                    durable: false,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null);

                return channel;
            }
            catch
            {
                await channel.DisposeAsync();
                throw;
            }
        }

        private async Task<string> StartCartConsumerAsync(
            IChannel channel)
        {
            var consumer = new AsyncEventingBasicConsumer(channel);

            consumer.ReceivedAsync += async (_, args) =>
            {
                try
                {
                    var message = Encoding.UTF8.GetString(
                        args.Body.ToArray());

                    var cart = JsonConvert.DeserializeObject<CartDto>(
                        message);

                    if (cart == null)
                    {
                        throw new InvalidOperationException(
                            "Cart message could not be deserialized.");
                    }

                    await _emailService.EmailCartAndLog(cart);

                    await channel.BasicAckAsync(
                        args.DeliveryTag, false);

                    Console.WriteLine(
                        "[EmailAPI] Cart-email message processed and acknowledged.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[EmailAPI] Cart-email processing failed: {ex}");

                    await channel.BasicNackAsync(
                        args.DeliveryTag, false, true);
                }
            };

            var tag = await channel.BasicConsumeAsync(
                queue: _emailCartQueue,
                autoAck: false,
                consumer: consumer);

            Console.WriteLine(
                $"[EmailAPI] Listening for cart-email messages on '{_emailCartQueue}'.");

            return tag;
        }

        private async Task<string> StartRegistrationConsumerAsync(
            IChannel channel)
        {
            var consumer = new AsyncEventingBasicConsumer(channel);

            consumer.ReceivedAsync += async (_, args) =>
            {
                try
                {
                    var message = Encoding.UTF8.GetString(
                        args.Body.ToArray());

                    var user = JsonConvert.DeserializeObject<UserDto>(
                        message);

                    if (user == null)
                    {
                        throw new InvalidOperationException(
                            "Registration message could not be deserialized.");
                    }

                    await _emailService.RegisterUserEmailAndLog(user);

                    await channel.BasicAckAsync(
                        args.DeliveryTag, false);

                    Console.WriteLine(
                        "[EmailAPI] Registration message processed and acknowledged.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[EmailAPI] Registration processing failed: {ex}");

                    await channel.BasicNackAsync(
                        args.DeliveryTag, false, true);
                }
            };

            var tag = await channel.BasicConsumeAsync(
                queue: _registerUserQueue,
                autoAck: false,
                consumer: consumer);

            Console.WriteLine(
                $"[EmailAPI] Listening for registration messages on '{_registerUserQueue}'.");

            return tag;
        }

        private async Task<string> StartOrderCreatedConsumerAsync(
            IChannel channel)
        {
            var consumer = new AsyncEventingBasicConsumer(channel);

            consumer.ReceivedAsync += async (_, args) =>
            {
                try
                {
                    var message = Encoding.UTF8.GetString(
                        args.Body.ToArray());

                    var orderEvent =
                        JsonConvert.DeserializeObject<OrderCreatedEventDto>(
                            message);

                    if (orderEvent == null)
                    {
                        throw new InvalidOperationException(
                            "OrderCreated event could not be deserialized.");
                    }

                    if (orderEvent.OrderId <= 0 ||
                        string.IsNullOrWhiteSpace(
                            orderEvent.CustomerEmail))
                    {
                        throw new InvalidOperationException(
                            "OrderCreated event has an invalid order ID or customer email.");
                    }

                    Console.WriteLine(
                        $"[EmailAPI] Received OrderCreated event. " +
                        $"EventId: {orderEvent.EventId}, " +
                        $"OrderId: {orderEvent.OrderId}");

                    await _emailService.OrderCreatedAndLog(orderEvent);

                    await channel.BasicAckAsync(
                        args.DeliveryTag, false);

                    Console.WriteLine(
                        $"[EmailAPI] OrderCreated event processed and acknowledged. " +
                        $"OrderId: {orderEvent.OrderId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[EmailAPI] OrderCreated processing failed: {ex}");

                    await channel.BasicNackAsync(
                        args.DeliveryTag, false, true);
                }
            };

            var tag = await channel.BasicConsumeAsync(
                queue: _orderCreatedEmailQueue,
                autoAck: false,
                consumer: consumer);

            Console.WriteLine(
                $"[EmailAPI] Listening for OrderCreated messages on '{_orderCreatedEmailQueue}'.");

            return tag;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            // Stop each consumer before disposing its channel.
            await CancelConsumerAsync(
                _cartChannel, _cartConsumerTag);

            await CancelConsumerAsync(
                _registrationChannel, _registrationConsumerTag);

            await CancelConsumerAsync(
                _orderCreatedChannel, _orderCreatedConsumerTag);

            await DisposeRabbitMQResourcesAsync();

            Console.WriteLine(
                "[EmailAPI] RabbitMQ consumers stopped.");
        }

        private static async Task CancelConsumerAsync(
            IChannel? channel, string? consumerTag)
        {
            if (channel?.IsOpen == true &&
                !string.IsNullOrWhiteSpace(consumerTag))
            {
                await channel.BasicCancelAsync(consumerTag);
            }
        }

        private async Task DisposeRabbitMQResourcesAsync()
        {
            if (_cartChannel != null)
            {
                await _cartChannel.DisposeAsync();
                _cartChannel = null;
            }

            if (_registrationChannel != null)
            {
                await _registrationChannel.DisposeAsync();
                _registrationChannel = null;
            }

            if (_orderCreatedChannel != null)
            {
                await _orderCreatedChannel.DisposeAsync();
                _orderCreatedChannel = null;
            }

            if (_connection != null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }
        }
    }
}
