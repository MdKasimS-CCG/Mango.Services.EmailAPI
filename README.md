# Mango.Services.EmailAPI

EmailAPI is a background message-consuming microservice in the Mango application. It consumes registration and cart-email messages from RabbitMQ and records email activity in SQL Server. The supplied implementation builds message content and logs it; SMTP or another outbound email transport is not configured in the reviewed source.

## Contents

- [Overview](#overview)
- [Technology](#technology)
- [Message flow](#message-flow)
- [Configuration](#configuration)
- [Prerequisites](#prerequisites)
- [Run locally](#run-locally)
- [Run with Docker](#run-with-docker)
- [Run with Docker using any terminal] (#run-via-any-terminal)
- [Run in the full Mango Compose stack](#run-in-the-full-mango-compose-stack)
- [CI/CD](#cicd)
- [Persistence and observability](#persistence-and-observability)
- [Troubleshooting](#troubleshooting)

## Overview

EmailAPI subscribes to RabbitMQ queues for cart-email and user-registration messages. It deserializes incoming payloads into DTOs, calls the email service to construct message content and write an `EmailLogger` record, then acknowledges the message on success. On exceptions in the consumer handler, it negatively acknowledges and requests requeue.

The current implementation is a message consumer, not a public email-sending REST API.

## Technology

- .NET 10 / ASP.NET Core host
- RabbitMQ.Client
- Entity Framework Core and SQL Server
- `DotNetEnv` for local `.env` loading
- Docker and GitHub Actions
- Private `Mango.MessageBus` package is referenced by the project, although the shown consumer implementation uses RabbitMQ.Client directly

## Message flow

```text
Mango services
   |
   | publish serialized messages
   v
RabbitMQ queues
   |-- EmailShoppingCartQueue
   |-- RegisterUserQueue
   |
   v
EmailAPI RabbitMQMessageConsumer
   |
   +--> EmailService builds message content
   |
   +--> SQL Server: EmailLoggers table
```

At startup, EmailAPI configures the consumer based on `MessageQueue:Provider`. The current implemented provider path is RabbitMQ. The consumer declares the configured queues and begins consuming both queue types when the application starts.

**Email delivery status:** the supplied `EmailService` constructs message bodies and persists records in the `EmailLoggers` table. No SMTP client or external email provider call appears in the supplied implementation. Do not interpret a successful database log as proof that an email was delivered to an inbox.

## Configuration

The repository includes `.env.example`. Copy it to `.env` and set environment-specific values. Do not commit secrets.

The code chooses the `Http` configuration prefix when not running in a container and `Docker` when `DOTNET_RUNNING_IN_CONTAINER=true`. It reads:
- Common `MessageQueue:Provider`
- Prefix-specific SQL Server connection string
- Prefix-specific RabbitMQ connection string
- Queue names for cart email and user registration

The `.env.example` shows these key shapes:

```dotenv
ASPNETCORE_ENVIRONMENT=Development
ASPNETCORE_HTTP_PORTS=8080
MessageQueue__Provider=RabbitMQ

Http__ConnectionStrings__DefaultConnection=<local-sql-server-connection-string>
Http__MessageQueue__RabbitMQ__ConnectionString=<rabbitmq-uri>

Docker__ConnectionStrings__DefaultConnection=<container-sql-server-connection-string>
Docker__MessageQueue__RabbitMQ__ConnectionString=<rabbitmq-uri>
```

Also provide the two queue-name settings expected by the application configuration:
- `TopicAndQueueNames__EmailShoppingCartQueue`
- `TopicAndQueueNames__RegisterUserQueue`

Use queue names consistent with the message producers. For a RabbitMQ instance running as a separate Docker container on Docker Desktop, the URI host from a container should be `host.docker.internal` and the RabbitMQ port must be published on the host. From local host execution, use the host-published localhost port.

The current `.env.example` does not populate actual credentials or queue names; verify all required keys are present in your local `.env` and the deployment environment.

## Prerequisites

- .NET 10 SDK
- SQL Server reachable using the selected configuration prefix
- RabbitMQ reachable using the configured connection URI
- Queue names matching the publishing services
- Docker Desktop for container execution

## Run locally

1. Copy `.env.example` to `.env` and fill in database, RabbitMQ, provider, and queue-name values.
2. Configure the private GitHub Packages NuGet source if required by restore.
3. From the repository directory:

```bash
dotnet restore Mango.Services.EmailAPI.csproj
dotnet run --launch-profile http
```

The checked-in HTTP launch profile uses `http://localhost:5216`; Swagger is configured for Development. The service starts its RabbitMQ consumer when the application lifecycle signals that startup is complete.

The application applies pending EF Core migrations during startup. SQL Server must be available and the configured database login must have migration permissions.

## Run with Docker

The Dockerfile uses .NET 10 SDK/runtime images, listens on port 8080 inside the container, and restores packages using a BuildKit secret named `nugetconfig`.

From the repository directory:

```bash
docker buildx build \
  --secret id=nugetconfig,src="<path-to-NuGet.Config>" \
  -t mango-emailapi:local \
  --load .
```

Run with the project environment file:

```bash
docker run --rm --name mango-emailapi \
  --env-file .env \
  -p 5126:8080 \
  mango-emailapi:local
```

Use the Docker-prefixed settings in the `.env` file for container execution. A RabbitMQ connection failure during consumer initialization can prevent the consumer from starting and may cause application startup failure in the current implementation.

## Docker Setup Commands - Can Be Used Via Any Terminal

Building the image:
docker build --secret id=nugetconfig,src="$env:NUGET_CONFIG_PATH" -t mango-email-local:dev .

Running the container:
docker run --name mango-emailapi --env-file .env -p 5126:8080 mango-email-local:dev

## Run in the full Mango Compose stack

Use the standalone Compose file at the Mango solution root, rather than Visual Studio-generated Compose files.

The EmailAPI build context should be its project directory so the Dockerfile's project-relative `COPY` paths resolve. Example service shape:

```yaml
services:
  mango-email:
    build:
      context: ./Backend/Mango.Services.EmailAPI
      dockerfile: Dockerfile
      secrets:
        - nuget_config
    env_file:
      - ./Backend/Mango.Services.EmailAPI/.env
    environment:
      ASPNETCORE_HTTP_PORTS: "8080"

secrets:
  nuget_config:
    file: ${NUGET_CONFIG_PATH}
```

The root Compose file must provide `NUGET_CONFIG_PATH` and the corresponding BuildKit secret. Do not commit NuGet credentials.

When RabbitMQ and SQL Server run in separate Docker containers outside the Compose network, configure their container connection strings to use `host.docker.internal` and confirm their host ports are published. If the dependencies are services inside the same Compose network, use their Compose service names instead.

Run from the solution root:

```bash
docker compose -f docker-compose.yml config
docker compose -f docker-compose.yml build mango-email
docker compose -f docker-compose.yml up -d mango-email
docker compose -f docker-compose.yml logs -f mango-email
```

Replace `mango-email` and the Compose filename with the exact names used in the solution's root Compose file.

## CI/CD

The workflow is `.github/workflows/email-api.yaml`.

It runs on pushes to `feature/*` and `main`, and on pull requests targeting `main`. The workflow:
1. Checks out the repository and installs .NET 10.
2. Caches NuGet packages.
3. Adds the GitHub Packages feed using the workflow's `GITHUB_TOKEN`.
4. Restores and builds the project.
5. Explicitly skips automated tests because no test project exists in the repository.
6. Builds a Docker image using Buildx and passes the NuGet configuration as a BuildKit secret.
7. On pushes to `main`, logs in to GHCR and pushes an image tagged with the commit SHA. Pull-request image builds are not published.

The workflow's image repository is `ghcr.io/mdkasims-ccg/mango-services-emailapi`.

## Persistence and observability

The EF Core model stores email activity in `EmailLoggers`, with fields for an integer ID, email address, message content, and timestamp (`EmailSent`). Review application logs and SQL Server records when diagnosing processing.

The consumer uses manual acknowledgements. Successful processing is acknowledged; exceptions in the message handler trigger a negative acknowledgement with requeue enabled. Repeated failures can therefore lead to messages being delivered repeatedly.

## Troubleshooting

| Symptom | Checks |
|---|---|
| EmailAPI fails during startup with RabbitMQ connection error | Verify the selected `Http` or `Docker` RabbitMQ URI, credentials, host reachability, port mapping, and queue settings. The consumer connects during startup. |
| SQL migration fails | Check the active configuration prefix, SQL Server hostname/port, database name, credentials, and migration permissions. |
| Messages are not consumed | Confirm provider is `RabbitMQ`, queue names exactly match publisher settings, and producers publish to the same broker/vhost. |
| Messages repeatedly reappear | Inspect consumer exceptions and SQL Server connectivity; failed handler processing is negatively acknowledged with requeue. |
| No email arrives in an inbox | The current supplied implementation creates/logs message content in SQL Server; it does not show an outbound SMTP/provider delivery integration. |
| Private NuGet restore fails in Docker | Ensure BuildKit is enabled and the `nugetconfig` secret is passed to the build. |