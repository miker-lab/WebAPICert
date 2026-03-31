# Copilot instructions for this repository

## Build, test, and run commands

Run commands from repository root.

- Restore: `dotnet restore WebAPICert.slnx`
- Build: `dotnet build WebAPICert.slnx`
- Run API: `dotnet run --project WebAPICert/WebAPICert.csproj`
- Test suite: `dotnet test WebAPICert.slnx`

Single-test command pattern (when test projects exist):

- `dotnet test <path-to-test-project.csproj> --filter "FullyQualifiedName~Namespace.ClassName.TestName"`

Linting:

- No dedicated lint command is configured in this repo today.

## High-level architecture

This is a single-project ASP.NET Core Web API (`WebAPICert/WebAPICert.csproj`, `net10.0`).

- `Program.cs` wires the app: controller services, OpenAPI service registration, middleware, and controller endpoint mapping.
- HTTP requests flow through HTTPS redirection and authorization middleware, then into attribute-routed controllers.
- `Controllers/WeatherForecastController.cs` currently contains the API endpoint (`GET /weatherforecast`).
- `WeatherForecast.cs` is the response model, including a computed `TemperatureF` property.
- OpenAPI endpoint mapping (`app.MapOpenApi()`) is enabled only in Development.
- Runtime/dev behavior is influenced by:
  - `Properties/launchSettings.json` (local URLs and `ASPNETCORE_ENVIRONMENT`)
  - `appsettings.json` and `appsettings.Development.json` (logging and host settings)
  - `WebAPICert.http` (manual endpoint smoke-test request)

## Key conventions in this codebase

- Use controller-based APIs (not Minimal APIs) and keep route definitions on controllers via attributes.
- Preserve file-scoped namespaces (e.g., `namespace WebAPICert.Controllers;`).
- Keep nullable-reference behavior enabled (`<Nullable>enable</Nullable>`) and avoid introducing nullable-oblivious code.
- Keep implicit usings enabled (`<ImplicitUsings>enable</ImplicitUsings>`); avoid unnecessary explicit framework usings.
- Keep OpenAPI behavior environment-gated as in `Program.cs` (`MapOpenApi` only in Development).
- Keep local port assumptions consistent between `launchSettings.json` and `WebAPICert.http`.
