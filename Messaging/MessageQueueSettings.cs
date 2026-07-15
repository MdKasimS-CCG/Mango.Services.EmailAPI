using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Mango.Services.EmailAPI.Messaging
{
    public class MessageQueueSettings
    {
        public string Provider { get; set; }
        public RabbitMqSettings RabbitMQ { get; set; }
        public AzureServiceBusSettings AzureServiceBus { get; set; }
        public KafkaSettings Kafka { get; set; }
    }

    public class RabbitMqSettings
    {
        public string ConnectionString { get; set; }
    }

    public class AzureServiceBusSettings
    {
        public string ConnectionString { get; set; }
    }

    public class KafkaSettings
    {
        public string BootstrapServers { get; set; }
    }
}
