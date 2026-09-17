//using Mango.MessageBus;
using Mango.Services.EmailAPI.Data;
using Mango.Services.EmailAPI.Extension;
using Mango.Services.EmailAPI.Messaging;
using Mango.Services.EmailAPI.Services;

//TODO: Check induvidual container comms with MSSQL and RabbitMQ.
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

// Determine whether the application is running inside a Docker container.
bool isRunningInContainer =
    Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
// The HTTP profile does not automatically load .env, so we load it
// manually before creating the WebApplicationBuilder.
if (!isRunningInContainer)
{
    LoadEnvFile(".env");
}

var builder = WebApplication.CreateBuilder(args);

// HTTP profile  -> Http
// Docker profile -> Docker
string configurationPrefix = isRunningInContainer
    ? "Docker"
    : "Http";

var emailOptions = new EmailOptions
{
    // Common configuration
    MessageQueueProvider =
        builder.Configuration[
            "MessageQueue:Provider"]
        ?? string.Empty,


    // Environment-specific SQL Server connection string
    DefaultConnection =
        builder.Configuration[
            $"{configurationPrefix}:ConnectionStrings:DefaultConnection"]
        ?? string.Empty,


    // Environment-specific RabbitMQ connection string
    RabbitMQConnectionString =
        builder.Configuration[
            $"{configurationPrefix}:MessageQueue:RabbitMQ:ConnectionString"]
        ?? string.Empty,


    // Common queue names
    EmailShoppingCartQueue =
        builder.Configuration[
            "TopicAndQueueNames:EmailShoppingCartQueue"]
        ?? string.Empty,

    RegisterUserQueue =
        builder.Configuration[
            "TopicAndQueueNames:RegisterUserQueue"]
        ?? string.Empty
};

builder.Services.AddSingleton(
    Microsoft.Extensions.Options.Options.Create(emailOptions));

builder.Configuration.AddInMemoryCollection(
    new Dictionary<string, string?>
    {
        // Database
        ["ConnectionStrings:DefaultConnection"] =
            emailOptions.DefaultConnection,


        // Message Queue
        ["MessageQueue:Provider"] =
            emailOptions.MessageQueueProvider,

        ["MessageQueue:RabbitMQ:ConnectionString"] =
            emailOptions.RabbitMQConnectionString,


        // Topic and Queue Names
        ["TopicAndQueueNames:EmailShoppingCartQueue"] =
            emailOptions.EmailShoppingCartQueue,

        ["TopicAndQueueNames:RegisterUserQueue"] =
            emailOptions.RegisterUserQueue
    });

builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    var settings = serviceProvider
        .GetRequiredService<IOptions<EmailOptions>>()
        .Value;

    options.UseSqlServer(settings.DefaultConnection);
});


var optionBuilder = new DbContextOptionsBuilder<AppDbContext>();

optionBuilder.UseSqlServer(
    emailOptions.DefaultConnection);

builder.Services.AddSingleton(
    new EmailService(optionBuilder.Options));

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

void LoadEnvFile(string fileName)
{
    var envPath = Path.Combine(
        Directory.GetParent(AppContext.BaseDirectory)!
            .Parent!
            .Parent!
            .Parent!
            .FullName,
        fileName);

    if (!File.Exists(envPath))
    {
        throw new FileNotFoundException(
            $"The environment file '{fileName}' was not found.",
            envPath);
    }

    foreach (var line in File.ReadAllLines(envPath))
    {
        var trimmedLine = line.Trim();


        // Ignore blank lines and comments.
        if (string.IsNullOrWhiteSpace(trimmedLine) ||
            trimmedLine.StartsWith("#"))
        {
            continue;
        }


        // Support optional "export KEY=value".
        if (trimmedLine.StartsWith("export "))
        {
            trimmedLine =
                trimmedLine["export ".Length..].Trim();
        }


        // Find the first '='.
        //
        // This is important because connection strings can
        // themselves contain '=' characters.
        var separatorIndex =
            trimmedLine.IndexOf('=');

        if (separatorIndex <= 0)
        {
            continue;
        }


        // Extract key.
        var key =
            trimmedLine[..separatorIndex].Trim();


        // Extract value.
        var value =
            trimmedLine[(separatorIndex + 1)..].Trim();


        // Remove surrounding quotes if present.
        if (value.Length >= 2 &&
            ((value.StartsWith('"') &&
              value.EndsWith('"')) ||
             (value.StartsWith('\'') &&
              value.EndsWith('\''))))
        {
            value = value[1..^1];
        }


        // Add the value to the process environment.
        Environment.SetEnvironmentVariable(
            key,
            value);
    }
}


public class EmailOptions
{
    // SQL Server
    public string DefaultConnection { get; set; } =
        string.Empty;


    // RabbitMQ
    public string RabbitMQConnectionString { get; set; } =
        string.Empty;


    // Message queue provider
    public string MessageQueueProvider { get; set; } =
        string.Empty;


    // Queue names
    public string EmailShoppingCartQueue { get; set; } =
        string.Empty;

    public string RegisterUserQueue { get; set; } =
        string.Empty;
}