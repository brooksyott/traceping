using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Peamel.NetworkUtilities;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Peamel.NetworkUtilities
{
    public partial class TracePingService
    {
        // Configuration properties
        public String OutputDirectory = @".";
        public String HostName = String.Empty;
        public String DisplayHostname = String.Empty;
        public Boolean ResolveHostNames = false;
        public int MaxHops = 50;
        public int DiscoveryTimeout = 3000;
        public int PingTimeout = 1000;
        public int PingFrequency = 1000;
        public int SaveFrequency = 60000;

        private readonly ILogger<TracePingService> _logger;
        static TracePing pTraceRt = null;

        Boolean FirstRun = true;
        // Stopwatches for timing
        Stopwatch DisplayStopWatch = new Stopwatch();
        Stopwatch SaveToFileSw = new Stopwatch();

        long DisplayFrequency = 5000;
        DateTime StartRunTime = DateTime.Now;
        DateTime LastFileSave = DateTime.Now;

        // CSV file handling
        string CsvFile = String.Empty;
        StreamWriter CsvFileHandler = null;

        // Constructor
        public TracePingService()
        {
            var serviceCollection = new ServiceCollection();

            // Configure logging with Serilog
            serviceCollection.AddLogging(configure => configure.AddSerilog())
            .AddTransient<TracePingService>();
            var _serviceProvider = serviceCollection.BuildServiceProvider();
            if (_serviceProvider == null)
            {
                throw new ArgumentNullException(nameof(_serviceProvider));
            }

            // Get logger service
#pragma warning disable CS8601 // Possible null reference assignment.
            _logger = _serviceProvider.GetService<ILogger<TracePingService>>();
#pragma warning restore CS8601 // Possible null reference assignment.

            if (_logger == null)
            {
                throw new ArgumentNullException(nameof(_logger));
            }
        }

        // Main execution method
        public void Execute()
        {
            _logger.LogInformation($"Executing traceping");

            BuildDisplayName();
            InitializeTracePing();
            InitCsvFile();
            StartPingTrace();

            Boolean dontQuit = true;

            // Wait on user prompt
            while (dontQuit)
            {
                ConsoleKeyInfo entry = Console.ReadKey();

                // If "q", quit the app
                if (entry.KeyChar.ToString() == "q")
                {
                    SaveToCsv();
                    pTraceRt.PingRoutesStop();
                    CsvFileHandler.Close();
                    dontQuit = false;
                }

                // If "c", clear the console stats
                if (entry.KeyChar.ToString() == "c")
                {
                    pTraceRt.ClearStats();
                }
            }

            return;
        }

        // Start the ping trace
        void StartPingTrace()
        {
            IEnumerable<TracePingResult> pingResult = null;

            Console.WriteLine($"Getting routes to {DisplayHostname}");
            Console.WriteLine($"Trace hop timeout: " + DiscoveryTimeout);

            // Start the stats stop watches
            SaveToFileSw.Start();
            DisplayStopWatch.Start();

            //pTraceRt.GetRoutesICMP();
            //pTraceRt.GetRoutesUDP();
            pingResult = pTraceRt.GetRoutes();

            SaveToFileSw.Start();
#pragma warning disable 4014
            Task.Run(async () => await pTraceRt.PingRoutesContinous(PingFrequency));
#pragma warning restore 4014
        }

        // Build display name for the host
        void BuildDisplayName()
        {
            try
            {
                var host = Dns.GetHostEntry(HostName);
                DisplayHostname = $"{HostName} [{host?.AddressList[0]}]";
            }
            catch (Exception)
            {
                DisplayHostname = HostName;
            }
        }

        // Initialize TracePing object
        void InitializeTracePing()
        {
            try
            {
                pTraceRt = new TracePing(HostName, MaxHops, DiscoveryTimeout, PingTimeout)
                {
                    // Disable getting host names. It really slows down initialization
                    GetHostName = false
                };
            }
            catch (Exception ee)
            {
                Console.WriteLine("Exception: " + ee.Message);
                Environment.Exit(1);
            }

            // Register event handlers
            pTraceRt.PingCompleteEvent += PingCompleteConsoleHandler;
            pTraceRt.PingCompleteEvent += PingCompleteCsvHandler;
        }



        // Event handler for console output
        void PingCompleteConsoleHandler(object? s, EventArgs e)
        {
            if (DisplayStopWatch.ElapsedMilliseconds < DisplayFrequency)
            {
                return;
            }

            // Restart the stopwatch
            DisplayStopWatch.Restart();

            TracePingStats[] lastRun = pTraceRt.LastPingResultConsole();
            Console.Clear();
            String header = TracePingStats.HeaderToConsole();

            Console.Clear();
            Console.WriteLine($"Routes for {DisplayHostname}");
            Console.WriteLine();
            Console.WriteLine(header);
            foreach (TracePingStats te in lastRun)
            {
                Console.WriteLine(te.DataToConsole());
            }

            Console.WriteLine();
            Console.WriteLine($"========================================================");
            Console.WriteLine($"  Ping Frequency (ms):  " + PingFrequency);
            Console.WriteLine($"  Start Time:           " + StartRunTime);
            Console.WriteLine($"  Refreshed:            " + DateTime.Now);
            Console.WriteLine($"  Saved:                " + LastFileSave);
            Console.WriteLine($"========================================================");
            Console.WriteLine("     q = quit       c = clear stats");
            Console.WriteLine($"========================================================");
        }


        // Initialize CSV file
        void InitCsvFile()
        {
            // Build the output file
            String dateString = DateTime.Now.ToString("yyyy-MM-dd");
            CsvFile = $"{OutputDirectory}\\traceping-{HostName}-{dateString}.csv";

            if (SaveFrequency > 0)
            {
                CsvFileHandler = File.AppendText(CsvFile);
                CsvFileHandler.AutoFlush = true;
                String csvHeader = TracePingStats.HeaderToCsv();
                CsvFileHandler.WriteLine(csvHeader);
                CsvFileHandler.Flush();
            }
        }

        // Event handler for CSV output
        void PingCompleteCsvHandler(Object? s, EventArgs e)
        {
            if (SaveFrequency == 0)
                return;

            if ((SaveToFileSw.ElapsedMilliseconds > SaveFrequency) || FirstRun)
            {
                SaveToCsv();
                SaveToFileSw.Restart();
                FirstRun = false;
            }
        }

        // Save data to CSV file
        private void SaveToCsv()
        {
            TracePingStats[] lastRun = pTraceRt.LastPingResultCsv();

            // Not thread safe. Could be an issue later on
            // TODO: Make thread safe
            foreach (TracePingStats te in lastRun)
            {
                String csvData = te.DataToCsv();
                CsvFileHandler.WriteLine(csvData);
            }

            CsvFileHandler.Flush();
            LastFileSave = DateTime.Now;
            pTraceRt.ClearStatsCsv();
        }
    }
}
