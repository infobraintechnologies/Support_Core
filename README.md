# CBS Support System

## Overview
CBS Support is a modern support system built using .NET technologies. The solution consists of multiple projects:

- **CBSSupport.API**: A web API built with ASP.NET Core that handles backend operations and real-time communication using SignalR
- **CBSSupport.MAUIApp**: A cross-platform mobile/desktop application built with .NET MAUI
- **CBSSupport.Shared**: A shared library containing common models and utilities

## Technologies Used

- .NET 10.0 LTS
- ASP.NET Core
- .NET MAUI
- SignalR for real-time communication
- PostgreSQL with Dapper ORM
- Bootstrap for UI styling

## Prerequisites

- .NET 10.0 SDK (the repository pins a supported SDK in `global.json`)
- Visual Studio 2022 or later (recommended)
- PostgreSQL database server
- For MAUI development:
  - Windows 10 version 1809 or higher for Windows development
  - macOS Catalina (10.15) or higher for iOS/macOS development
  - Android SDK for Android development

## Setup Instructions

1. **Clone the Repository**
   ```bash
   git clone [repository-url]
   cd [repository-name]
   ```

2. **Database Setup**
   - Install PostgreSQL if not already installed
   - Create a new database
   - Update the connection string in your configuration

3. **API Project Setup**
   ```bash
   cd CBSSupport.API
   dotnet restore
   dotnet run
   ```

4. **MAUI App Setup**
   ```bash
   cd CBSSupport.MAUIApp
   dotnet restore
   dotnet build
   ```

5. **Running the Application**
   - For API: The application will be available at `https://localhost:5001`
   - For MAUI app: Run through Visual Studio or use `dotnet run`

## Project Structure

- `CBSSupport.API/`
  - Web API project with SignalR hubs
  - Database interactions using Dapper
  - RESTful endpoints

- `CBSSupport.MAUIApp/`
  - Cross-platform UI application
  - SignalR client integration
  - Responsive UI design

- `CBSSupport.Shared/`
  - Shared models
  - Common utilities
  - DTOs for data transfer

## Features

- Real-time communication using SignalR
- Cross-platform support (Windows, Android, iOS, macOS)
- Modern responsive UI
- Secure API endpoints
- Database integration with PostgreSQL

## Development Notes

- Ensure all required SDKs are installed for the platforms you're targeting
- Use Visual Studio 2022 for the best development experience
- Keep the shared library clean and focused on common functionality
- Follow the existing code style and patterns
- OpenAPI document generation remains intentionally deferred while the legacy OpenAPI stack is retained for planned removal.

### SignalR deployment mode

The current supported deployment mode is:

```text
SignalR__DeploymentMode=SingleInstance
```

The built-in ASP.NET Core SignalR lifetime manager is sufficient for the
current single-API-instance deployment. Authentication, security-stamp
validation, server-controlled tenant/conversation membership, and local
revocation connection tracking remain enabled.

Multiple API replicas are currently unsupported. Before horizontally scaling
the API, the company must select and approve a distributed SignalR mechanism;
sticky sessions alone do not provide cross-instance message propagation. See
`.context/SIGNALR_SCALE_OUT.md` for the deployment decision and deferred work.

## Contributing

1. Fork the repository
2. Create your feature branch
3. Commit your changes
4. Push to the branch
5. Create a new Pull Request

