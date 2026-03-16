# nocscienceat.JavaSimulatorOVH

Java Simulator AIRBUS OVH PANEL Size:M implementation for X-Plane based on the nocscienceat.XPlanePanel, nocscienceat.XPlaneWebConnector framework.


## Installation
```bash
dotnet add package nocscienceat.JavaSimulatorOVH
```

## Usage

This library provides panel handlers for Java Simulator overhead hardware, integrating with X-Plane through the nocscienceat.XPlanePanel framework.

### Basic Setup

Create a hosted service application with the following `Program.cs`:

```csharp
using nocscienceat.JavaSimulatorOVH.Panels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using nocscienceat.XPlanePanel;

var builder = Host.CreateApplicationBuilder(args);

// Configuration
builder.Configuration
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile("datarefs.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("XP_");

// Core services
builder.Services.AddXPlaneWebConnector(builder.Configuration);
builder.Services.AddXPlanePanel();

// Panel handlers (register all, filter by config at runtime)
builder.Services.AddSingleton<IPanelHandler, OvhPanelHandler>();

// Logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var host = builder.Build();
await host.RunAsync();
```

### Configuration

Create an `appsettings.json` file with your X-Plane Web Connector settings and panel configuration:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "JS_Connect": "Debug",
      "System.Net.Http.HttpClient": "Warning"
    }
  },
  "XPlane": {
    "IpAddress": "127.0.0.1",
    "WebPort": 8086,
    "ReadinessProbeDataRef": "AirbusFBW/BatVolts",
    "ReadinessProbeMaxRetries": 0,
    "Transport": "Http",
    "FireForgetOnHttpTransport": true,
    "ApiVersion": "v2"
  },
  "Panels": {
    "OVH": {
      "Enabled": true,
      "PortName": "COMx",
      "BaudRate": 19200
    }
  }
}
```

### DataRefs Configuration (Optional)

Optionally create a `datarefs.json` file for dataref override (panel includes required dataref and command definitions):

```json
{
  "XplaneDataRefsCommands": {
    "OVH": {
      "DataRefs": {
        // uncomment the following 2 lines to display left voltage on the right display and right voltage on left display as an example
        // to override the datarefs as defined in the panel implementation.
        //"BAT1_V": "AirbusFBW/BatVolts[1]",
        //"BAT2_V": "AirbusFBW/BatVolts[0]"
      },
      "Commands": {
      }
    }
  }
}
```

## License
MIT License - see LICENSE.txt

## Repository
https://github.com/klemensurban/nocscienceat.JavaSimulatorOVH
