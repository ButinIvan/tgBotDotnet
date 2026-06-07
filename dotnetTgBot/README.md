# tgSchoolBot

ASP.NET Core MVC application with a Telegram bot for school class communication.

The app provides:

- Telegram registration for parents, admins, and moderators.
- Class-based news and report publishing.
- Web admin panel for news, reports, parents, moderators, and verification requests.
- PostgreSQL persistence.
- RabbitMQ-backed news delivery.
- MinIO-backed report file storage.

## Requirements

- Docker and Docker Compose.
- Telegram bot token from BotFather.

For local .NET development without Docker, install .NET SDK 8.0 or newer.

## Configuration

Copy the example environment file:

```powershell
Copy-Item .env.example .env
```

Then edit `.env` and set real values:

- `TELEGRAM_BOT_TOKEN`
- `DB_PASSWORD`
- `RABBITMQ_PASSWORD`
- `MINIO_SECRET_KEY`

Optional public URLs:

- `ADMIN_PANEL_URL` is shown by the bot in `/adminpanel`.
- `MINIO_PUBLIC_ENDPOINT` is used when generating public report download links.
- `APP_TIME_ZONE` controls displayed dates. The default is `Europe/Moscow`.

## Run With Docker

From the `dotnetTgBot` directory:

```powershell
docker compose up --build
```

Services:

- Admin panel: `http://localhost:5010`
- Health endpoint: `http://localhost:5010/health`
- PostgreSQL host port: `5433`
- RabbitMQ management UI: `http://localhost:15672`
- MinIO console: `http://localhost:9001`

## Local Build

```powershell
dotnet restore dotnetTgBot.sln
dotnet build dotnetTgBot.sln
```

## Notes

- The app applies EF Core migrations on startup.
- Required environment variables are validated at startup.
- Report uploads are limited to 10 MB and common document/image extensions.
- The current admin login form still uses Telegram User ID. Before real production use, replace it with Telegram Login Widget or a one-time code flow through the bot.
