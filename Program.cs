//using Mango.MessageBus;
using Mango.Services.EmailAPI.Data;
using Mango.Services.EmailAPI.Extension;
using Mango.Services.EmailAPI.Messaging;
using Mango.Services.EmailAPI.Services;

using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

var optionBuilder = new DbContextOptionsBuilder<AppDbContext>();
optionBuilder.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"));
builder.Services.AddSingleton(new EmailService(optionBuilder.Options));

builder.Services.Configure<MessageQueueSettings>(
    builder.Configuration.GetSection("MessageQueue"));

var mqSettings = builder.Configuration
    .GetSection("MessageQueue")
    .Get<MessageQueueSettings>();

if (mqSettings.Provider == "RabbitMQ")
{
    //TODO: Singleton service cannot use scoped services. DbContext is scoped service. Hence need variation of DbContext.
    builder.Services.AddSingleton<IMessageConsumer, RabbitMQMessageConsumer>();
}
/// Note: Will be used when ServiceBusConsumer will be created
/*
 * 
 * else if (mqSettings.Provider == "AzureServiceBus")
 * {
 * builder.Services.AddSingleton<IMessageBus, AzureServiceBusMessageConsumer>();
 * 
 * }
 */

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

ApplyMigration();

app.UseRabbitMQMessageConsumer();

app.Run();

void ApplyMigration()
{
    using (var scope = app.Services.CreateScope())
    {
        var _db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (_db.Database.GetPendingMigrations().Count() > 0)
        {
            _db.Database.Migrate();
        }
    }
}