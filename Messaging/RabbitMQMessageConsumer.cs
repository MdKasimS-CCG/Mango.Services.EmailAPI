using Mango.Services.EmailAPI.Models.Dto;
using Mango.Services.EmailAPI.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

using Newtonsoft.Json;

using RabbitMQ.Client;
using RabbitMQ.Client.Events;

using System.Data.Common;
using System.Text;
using System.Threading.Channels;

namespace Mango.Services.EmailAPI.Messaging
{
    //TODO: Make it as worker service - Inherit BackgroundService.
    //Note: Whenever EmailAPI spins up, Consumers are also spinning. If they couldn't, application is crashing in VS2026 Debug mode at least.
    public class RabbitMQMessageConsumer: IMessageConsumer
    {
        private readonly string _messageBusConnectionString;
        private readonly string _emailCartQueue;
        private readonly string _registerUserQueue;
        private readonly IConfiguration _configuration;

        //TODO: Because it is singleton, we don't need interface
        private readonly EmailService _emailService;
        private IConnection _connection;
        private IChannel _channel;

        public RabbitMQMessageConsumer(IOptions<MessageQueueSettings> options
                                        , IConfiguration configuration
                                        , EmailService emailService)
        {
            _configuration = configuration ?? throw new InvalidOperationException("RabbitMQ connection string missing.");
            _emailService = emailService;
            _messageBusConnectionString = options.Value.RabbitMQ.ConnectionString;

            /* TODO: Here GetValue was not working somehow
             * _emailCartQueue = _configuration.GetValue<string>("Key");
             */
            _emailCartQueue = configuration["TopicAndQueueNames:EmailShoppingCartQueue"] ?? throw new InvalidOperationException("EmailShoppingCartQueue config missing."); ;

            _registerUserQueue = configuration["TopicAndQueueNames:RegisterUserQueue"] ?? throw new InvalidOperationException("RegisterUserQueue config missing."); ;

            //_orderCreatedQueue =
            //    configuration["TopicAndQueueNames:OrderCreatedTopic"]?? throw new InvalidOperationException("OrderCreatedQueue config missing.");;

            // TODO: Refactor it. Not satisfied with this arrangements. The reason is - if async is available,
            // with this setting it remains unused.
            var factory = new ConnectionFactory()
            {
                Uri = new Uri(_messageBusConnectionString)
            };

            //TODO: sync-over-async and can deadlock (and it hides failures during DI construction)
            //TODO: Per queue, you can create channel instead of single one. Scaling factor
            //TODO: When RabbitMQ is not running, it throws exception. Handle this exception. If its not reachable, whole application is crashing. Microservice arch?
            _connection = factory.CreateConnectionAsync().Result;
            _channel = _connection.CreateChannelAsync().Result;

            _channel.QueueDeclareAsync(_emailCartQueue, false, false, false, null);
            _channel.QueueDeclareAsync(_registerUserQueue, false, false, false, null);
            //_channel.QueueDeclareAsync(_orderCreatedQueue, false, false, false, null);
        }

        private async Task StartEmailCartConsumerAsync()
        {
            var consumer = new AsyncEventingBasicConsumer(_channel);

            consumer.ReceivedAsync += async (sender, args) =>
            {
                var body = args.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);

                var objMessage =
                    JsonConvert.DeserializeObject<CartDto>(message);

                try
                {
                    await _emailService.EmailCartAndLog(objMessage);
                    await _channel.BasicAckAsync(args.DeliveryTag, false);
                }
                catch
                {
                    // optionally requeue
                    await _channel.BasicNackAsync(args.DeliveryTag, false, true);
                }
            };

            await _channel.BasicConsumeAsync(
                queue: _emailCartQueue,
                autoAck: false,
                consumer: consumer);
        }

        public async Task Start()
        {
            await StartEmailCartConsumerAsync();
            await StartRegisterUserConsumerAsync();
            //await StartOrderPlacedConsumerAsync();
        }

        public async Task Stop()
        {
            await _channel.DisposeAsync();
            await _connection.DisposeAsync();
        }

        //Pass error handler from RabbitMQ documentation
        private Task ErrorHandler()
        {
            Console.WriteLine("");
            return Task.CompletedTask;
        }

        private async Task StartRegisterUserConsumerAsync()
        {
            var consumer = new AsyncEventingBasicConsumer(_channel);

            consumer.ReceivedAsync += async (sender, args) =>
            {
                var body = args.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);

                UserDto user =
                    JsonConvert.DeserializeObject<UserDto>(message);

                try
                {
                    await _emailService.RegisterUserEmailAndLog(user);
                    await _channel.BasicAckAsync(args.DeliveryTag, false);
                }
                catch
                {
                    await _channel.BasicNackAsync(args.DeliveryTag, false, true);
                }
            };

            await _channel.BasicConsumeAsync(
                queue: _registerUserQueue,
                autoAck: false,
                consumer: consumer);
        }

        //private async Task StartOrderPlacedConsumerAsync()
        //{
        //    var consumer = new EventingBasicConsumer(_channel);

        //    consumer.Received += async (sender, args) =>
        //    {
        //        var body = args.Body.ToArray();
        //        var message = Encoding.UTF8.GetString(body);

        //        RewardsMessage objMessage =
        //            JsonConvert.DeserializeObject<RewardsMessage>(message);

        //        try
        //        {
        //            await _emailService.LogOrderPlaced(objMessage);
        //            _channel.BasicAck(args.DeliveryTag, false);
        //        }
        //        catch
        //        {
        //           await _channel.BasicNack(args.DeliveryTag, false, true);
        //        }
        //    };

        //    await _channel.BasicConsume(
        //        queue: _orderCreatedQueue,
        //        autoAck: false,
        //        consumer: consumer);
        //}
    }
}
