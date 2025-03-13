using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Serilog.Extensions.Logging;
using System.CommandLine.Invocation;
using Peamel.NetworkUtilities;
using System.Net;

namespace TracePingConsole
{
    public partial class TracePingCli
    {
        private ServiceProvider? _serviceProvider;
        private ILogger<TracePingCli>? _logger;

        private readonly LoggingLevelSwitch levelSwitch = new LoggingLevelSwitch();


        public void ChangeLogLevel(LogEventLevel newLevel)
        {
            levelSwitch.MinimumLevel = newLevel;
        }

        public bool ChangeLogLevel(string newLevel)
        {
            string level = newLevel.ToLower();
            switch (level)
            {
                case "verbose":
                    ChangeLogLevel(LogEventLevel.Verbose);
                    break;
                case "debug":
                    ChangeLogLevel(LogEventLevel.Debug);
                    break;
                case "information":
                    ChangeLogLevel(LogEventLevel.Information);
                    break;
                case "warning":
                    ChangeLogLevel(LogEventLevel.Warning);
                    break;
                case "error":
                    ChangeLogLevel(LogEventLevel.Error);
                    break;
                case "fatal":
                    ChangeLogLevel(LogEventLevel.Fatal);
                    break;
                default:
                    ChangeLogLevel(LogEventLevel.Fatal);
                    return false;
            }

            return true;
        }

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddLogging(configure => configure.AddSerilog())
                    .AddTransient<TracePingCli>();
        }

        public RootCommand? CreateCommands()
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(levelSwitch)
                .WriteTo.Console(outputTemplate:
                    "{Timestamp:yy-MM-dd HH:mm:ss.fff}  {Level:u11} {SourceContext}: {Message}{NewLine}{Exception}")
                .CreateLogger();

            ChangeLogLevel(LogEventLevel.Error);

            var serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection);
            _serviceProvider = serviceCollection.BuildServiceProvider();

            if (_serviceProvider == null)
            {
                Console.WriteLine("Failed to build service provider");
                return null;
            }

            _logger = _serviceProvider.GetService<ILogger<TracePingCli>>();

            if (_logger == null)
            {
                Console.WriteLine("Failed to get logger");
                return null;
            }

            _logger.LogInformation("Starting Test Runner");


            var rootCommand = new RootCommand("Trace Ping - Traceroute plus ping");

            var loglevelOption = new Option<string>("--log-level", () => "error", "Configure logging level");
            loglevelOption.AddAlias("-l");
            rootCommand.AddGlobalOption(loglevelOption);

            var hostNameOption = new Option<string>("--host", "Either hostname or ip address");
            rootCommand.AddGlobalOption(hostNameOption);

            var resolveHostnameOption = new Option<bool>("--resolve-hostname", () => false, "Resolve the hostnames <true | false>");
            rootCommand.AddGlobalOption(resolveHostnameOption);

            var outputDirectoryOption = new Option<string>("--output-directory", () => ".", "Output directory for CSV file");
            rootCommand.AddGlobalOption(outputDirectoryOption);

            var maxHopsOption = new Option<int>("--max-hops", () => 50, "Maximum number of hops to trace (default: 50)");
            rootCommand.AddGlobalOption(maxHopsOption);

            var discoveryTimeoutOption = new Option<int>("--discovery-timeout", () => 3000, "Timeout for discovery in milliseconds");
            rootCommand.AddGlobalOption(discoveryTimeoutOption);

            var pingTimeoutOption = new Option<int>("--ping-timeout", () => 1000, "Timeout for ping in milliseconds");
            rootCommand.AddGlobalOption(pingTimeoutOption);

            var pingFrequencyOption = new Option<int>("--ping-frequency", () => 1000, "Frequency of pings in milliseconds");
            rootCommand.AddGlobalOption(pingFrequencyOption);

            var saveFrequencyOption = new Option<int>("--save-frequency", () => 60, "Frequency of saving to CSV file in seconds");
            rootCommand.AddGlobalOption(saveFrequencyOption);

            rootCommand.SetHandler(async (hostName, resolveHostNames, maxHops, discoveryTimeout, pingTimeout, pingFrequency, saveFrequency, outputDirectory) =>
            {
                TracePingService tracePingService = new TracePingService();

                if (hostName == null)
                {
                    Console.WriteLine("Host name is required");
                    return;
                }

                _logger.LogInformation("Executing traceping");
                tracePingService.HostName = hostName;
                tracePingService.ResolveHostNames = resolveHostNames;
                tracePingService.MaxHops = maxHops;
                tracePingService.DiscoveryTimeout = discoveryTimeout;
                tracePingService.PingTimeout = pingTimeout;
                tracePingService.PingFrequency = pingFrequency;
                tracePingService.SaveFrequency = saveFrequency * 1000;
                tracePingService.OutputDirectory = outputDirectory;

                tracePingService.Execute();

            }, hostNameOption, resolveHostnameOption, maxHopsOption, discoveryTimeoutOption, pingTimeoutOption, pingFrequencyOption, saveFrequencyOption, outputDirectoryOption);


            return rootCommand;
        }

    }
}