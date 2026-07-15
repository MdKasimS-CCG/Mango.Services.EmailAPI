namespace Mango.Services.EmailAPI.Messaging
{
    public interface IMessageConsumer
    {
        Task Start();
        Task Stop();
    }
}