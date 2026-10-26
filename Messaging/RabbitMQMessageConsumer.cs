using Mango.Services.EmailAPI.Models.Dto;
using Mango.Services.EmailAPI.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using System.Data.Common;
using System.Text;

namespace Mango.Services.EmailAPI.Messaging
{
    /// Runs as a BackgroundService so a broker that is not up yet does not
    /// crash the host (REST endpoints keep serving while we retry).
    /// Failure handling follows RewardAPI's OrderCreatedConsumer:
    /// permanent failures are dead-lettered, transient ones get a bounded retry.
    public sealed class RabbitMQMessageConsumer : BackgroundService
    {
        // Same layout as RewardAPI (reward.dlx -> rewardordercreated.dlq):
        // email.dlx routes "<queue>" to "<queue>.dlq" for each email queue.
        // Producers (Mango.MessageBus) must declare the main queues with the
        // same x-dead-letter-* arguments, or RabbitMQ rejects the declare (406).
        private const string DeadLetterExchange = "email.dlx";

        private const int MaxTransientAttempts = 3;
        private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan QueueMismatchRetryDelay = TimeSpan.FromSeconds(30);

        private readonly string _connectionString;
        private readonly string _emailCartQueue;
        private readonly string _registerUserQueue;
        private readonly string _orderCreatedEmailQueue;
        private readonly EmailService _emailService;

        private IConnection? _connection;
        private readonly List<(IChannel Channel, string ConsumerTag)> _consumers = new();

        public RabbitMQMessageConsumer(
            IOptions<EmailConsumerSettings> options,
            EmailService emailService)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(emailService);
            var settings = options.Value;

            _connectionString = settings.ConnectionString;
            _emailCartQueue = settings.EmailShoppingCartQueue;
            _registerUserQueue = settings.RegisterUserQueue;
            _orderCreatedEmailQueue = settings.OrderCreatedEmailQueue;

            _emailService = emailService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var factory = new ConnectionFactory
            {
                Uri = new Uri(_connectionString),
                // Reconnects (and re-registers consumers) after a drop post-startup.
                AutomaticRecoveryEnabled = true
            };

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await StartConsumersAsync(factory, stoppingToken);
                    break;
                }
                catch (BrokerUnreachableException ex)
                    when (!stoppingToken.IsCancellationRequested)
                {
                    Console.WriteLine(
                        $"[EmailAPI] Could not connect to RabbitMQ. Retrying in " +
                        $"{ConnectRetryDelay.TotalSeconds} seconds. {ex.Message}");

                    await DisposeRabbitMQResourcesAsync();
                    await Task.Delay(ConnectRetryDelay, stoppingToken);
                }
                catch (OperationInterruptedException ex)
                    when (ex.ShutdownReason?.ReplyCode == 406 &&
                          !stoppingToken.IsCancellationRequested)
                {
                    // PRECONDITION_FAILED: the queue already exists with different
                    // arguments, e.g. a producer declared it without dead-lettering.
                    // Retrying lets us recover once the queue is recreated.
                    Console.WriteLine(
                        $"[EmailAPI] A queue exists with arguments that do not match " +
                        $"the dead-letter setup: {ex.ShutdownReason?.ReplyText}. " +
                        $"Retrying in {QueueMismatchRetryDelay.TotalSeconds} seconds.");

                    await DisposeRabbitMQResourcesAsync();
                    await Task.Delay(QueueMismatchRetryDelay, stoppingToken);
                }
            }

            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // The host is stopping; the finally block closes broker resources.
            }
            finally
            {
                await StopConsumersAsync();
                await DisposeRabbitMQResourcesAsync();

                Console.WriteLine(
                    "[EmailAPI] RabbitMQ consumers stopped.");
            }
        }

        private async Task StartConsumersAsync(
            ConnectionFactory factory, CancellationToken stoppingToken)
        {
            _connection = await factory.CreateConnectionAsync(stoppingToken);

            // Each consumer owns its own channel.
            await StartConsumerAsync<CartDto>(
                _emailCartQueue,
                "Cart-email",
                validate: null,
                handle: cart => _emailService.EmailCartAndLog(cart),
                stoppingToken);

            await StartConsumerAsync<UserDto>(
                _registerUserQueue,
                "Registration",
                validate: null,
                handle: user => _emailService.RegisterUserEmailAndLog(user),
                stoppingToken);

            await StartConsumerAsync<OrderCreatedEventDto>(
                _orderCreatedEmailQueue,
                "OrderCreated",
                validate: orderEvent =>
                {
                    if (orderEvent.OrderId <= 0 ||
                        string.IsNullOrWhiteSpace(orderEvent.CustomerEmail))
                    {
                        throw new InvalidDataException(
                            "OrderCreated event has an invalid order ID or customer email.");
                    }
                },
                handle: orderEvent => _emailService.OrderCreatedAndLog(orderEvent),
                stoppingToken);

            Console.WriteLine(
                "[EmailAPI] All RabbitMQ consumers started.");
        }

        private async Task StartConsumerAsync<T>(
            string queueName,
            string label,
            Action<T>? validate,
            Func<T, Task> handle,
            CancellationToken stoppingToken)
        {
            var channel = await _connection!.CreateChannelAsync(
                cancellationToken: stoppingToken);

            try
            {
                await DeclareQueueWithDeadLetterAsync(
                    channel, queueName, stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);

                consumer.ReceivedAsync += async (_, args) =>
                {
                    try
                    {
                        var message = Encoding.UTF8.GetString(args.Body.Span);

                        var payload = JsonConvert.DeserializeObject<T>(message)
                            ?? throw new InvalidDataException(
                                $"{label} message was empty.");

                        validate?.Invoke(payload);

                        await HandleWithRetryAsync(
                            () => handle(payload), label, stoppingToken);

                        await channel.BasicAckAsync(
                            args.DeliveryTag, multiple: false);

                        Console.WriteLine(
                            $"[EmailAPI] {label} message processed and acknowledged.");
                    }
                    catch (OperationCanceledException)
                        when (stoppingToken.IsCancellationRequested)
                    {
                        // Shutting down mid-retry: leave the message unacked so
                        // RabbitMQ redelivers it when the channel closes.
                    }
                    catch (Exception ex)
                    {
                        // Permanent failure (bad JSON, failed validation) or a
                        // transient one that ran out of retries. Never requeue:
                        // RabbitMQ moves it to "<queue>.dlq" via email.dlx.
                        Console.WriteLine(
                            $"[EmailAPI] {label} processing failed; " +
                            $"dead-lettering to '{queueName}.dlq'. {ex}");

                        if (channel.IsOpen)
                        {
                            await channel.BasicNackAsync(
                                args.DeliveryTag, multiple: false, requeue: false);
                        }
                    }
                };

                var tag = await channel.BasicConsumeAsync(
                    queue: queueName,
                    autoAck: false,
                    consumer: consumer,
                    cancellationToken: stoppingToken);

                _consumers.Add((channel, tag));

                Console.WriteLine(
                    $"[EmailAPI] Listening for {label} messages on '{queueName}'.");
            }
            catch
            {
                await channel.DisposeAsync();
                throw;
            }
        }

        private static async Task DeclareQueueWithDeadLetterAsync(
            IChannel channel, string queueName, CancellationToken stoppingToken)
        {
            var deadLetterQueue = $"{queueName}.dlq";

            await channel.ExchangeDeclareAsync(
                exchange: DeadLetterExchange,
                type: ExchangeType.Direct,
                durable: true,
                autoDelete: false,
                arguments: null,
                cancellationToken: stoppingToken);

            await channel.QueueDeclareAsync(
                queue: deadLetterQueue,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                cancellationToken: stoppingToken);

            await channel.QueueBindAsync(
                queue: deadLetterQueue,
                exchange: DeadLetterExchange,
                routingKey: queueName,
                cancellationToken: stoppingToken);

            var queueArguments = new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = DeadLetterExchange,
                ["x-dead-letter-routing-key"] = queueName
            };

            await channel.QueueDeclareAsync(
                queue: queueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: queueArguments,
                cancellationToken: stoppingToken);
        }

        // Bounded retry for transient failures (1s, 2s back-off). Anything
        // else, or a transient failure on the last attempt, propagates and
        // the caller dead-letters the message.
        private static async Task HandleWithRetryAsync(
            Func<Task> action, string label, CancellationToken stoppingToken)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await action();
                    return;
                }
                catch (Exception ex)
                    when (attempt < MaxTransientAttempts && IsTransientFailure(ex))
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));

                    Console.WriteLine(
                        $"[EmailAPI] {label} attempt {attempt}/{MaxTransientAttempts} " +
                        $"failed with a transient error; retrying in " +
                        $"{delay.TotalSeconds} seconds. {ex.Message}");

                    await Task.Delay(delay, stoppingToken);
                }
            }
        }

        // Any database error counts as transient: SqlClient does not reliably
        // set DbException.IsTransient, and the retry is bounded, so a
        // misclassified permanent DB error only costs two extra attempts.
        private static bool IsTransientFailure(Exception exception)
        {
            for (var current = exception; current is not null; current = current.InnerException)
            {
                if (current is TimeoutException || current is DbException)
                    return true;
            }

            return false;
        }

        private async Task StopConsumersAsync()
        {
            // Stop each consumer before disposing its channel.
            foreach (var (channel, consumerTag) in _consumers)
            {
                if (channel.IsOpen)
                {
                    await channel.BasicCancelAsync(consumerTag);
                }
            }
        }

        private async Task DisposeRabbitMQResourcesAsync()
        {
            foreach (var (channel, _) in _consumers)
            {
                await channel.DisposeAsync();
            }

            _consumers.Clear();

            if (_connection != null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }
        }
    }
}
