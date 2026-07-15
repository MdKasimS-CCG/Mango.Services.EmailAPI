//using Mango.MessageBus;

using Mango.Services.EmailAPI.Messaging;

namespace Mango.Services.EmailAPI.Extension
{
    public static class ApplicationBuilderExtensions
    {
        private static IMessageConsumer _rabbitMqMessageConsumer { get; set; }

        //TODO: In extension class, why everything is static required. What special usecase is fro static constructor?
        public static IApplicationBuilder UseRabbitMQMessageConsumer(this IApplicationBuilder app)
        {
            _rabbitMqMessageConsumer = app.ApplicationServices.GetService<IMessageConsumer>();
            var hostApplicationLife = app.ApplicationServices.GetService<IHostApplicationLifetime>();

            hostApplicationLife.ApplicationStarted.Register(OnStart);
            hostApplicationLife.ApplicationStopping.Register(OnStop);

            return app;
        }

        private static void OnStop()
        {
            _rabbitMqMessageConsumer.Stop();
        }

        private static void OnStart()
        {
            _rabbitMqMessageConsumer.Start();
        }
    }
}
