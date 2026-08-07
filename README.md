# CBS Support System

## Overview
CBS Support is a modern support system built using .NET technologies. The solution consists of multiple projects:

- **CBSSupport.API**: A web API built with ASP.NET Core that handles backend operations and real-time communication using SignalR
- **CBSSupport.Shared**: A shared library containing common models and utilities
- **CBSSupport.API.Tests**: Unit, API, integration, and contract tests for the supported application

## Technologies Used

- .NET 10.0 LTS
- ASP.NET Core
- SignalR for real-time communication
- PostgreSQL with Dapper ORM
- Bootstrap for UI styling

## Prerequisites

- .NET 10.0 SDK (the repository pins a supported SDK in `global.json`)
- Visual Studio 2022 or later (recommended)
- PostgreSQL database server

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

4. **Running the Application**
   - For API: The application will be available at `https://localhost:5001`

## Project Structure

- `CBSSupport.API/`
  - Web API project with SignalR hubs
  - Database interactions using Dapper
  - RESTful endpoints

- `CBSSupport.Shared/`
  - Shared models
  - Common utilities
  - DTOs for data transfer

## Features

- Real-time communication using SignalR
- Responsive browser-based support workflows
- Modern responsive UI
- Secure API endpoints
- Database integration with PostgreSQL

## Development Notes

- Ensure the .NET 10 SDK and PostgreSQL are installed
- Use Visual Studio 2022 for the best development experience
- Keep the shared library clean and focused on common functionality
- Follow the existing code style and patterns
- Database changes are reviewed SQL scripts under `Database/Migrations`, with
  read-only checks under `Database/Preflight` and deployment run through pgAdmin
  or psql by an authorized operator.

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

