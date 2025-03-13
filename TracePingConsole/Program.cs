using System;
using System.CommandLine;
using System.Diagnostics;
using Peamel.NetworkUtilities;

namespace TracePingConsole;


class Program
{
    // Used to ensure we display the stats immediately when we first startup
    static Boolean FirstRun = true;
    static DateTime StartRunTime = DateTime.Now;

    // File handler for the CSV file
    static StreamWriter CsvFileHandler = null;
    static String CsvFile = String.Empty;
    static DateTime LastFileSave = DateTime.Now;

    // Pretty version of the host name
    static String DisplayHostname = String.Empty;

    // Stopwatch to keep track of time between refreshing the console display 
    static Stopwatch DisplayStopWatch = new Stopwatch();
    // maximum time to wait between refreshes of the console display
    static long DisplayFrequency = 5000;

    // Stopwatch to keep track of time between saving the stats to the CSV file 
    static Stopwatch SaveToFileSw = new Stopwatch();

    // Contains default values, and interacts the command line 
    // arguments to provide the overall configuration for the app
    static TracePingCli _configSrv = new TracePingCli();

    // class that implements the trace ping
    static TracePing pTraceRt = null;


    static async Task<int> Main(string[] args)
    {
        var tracePingCli = new TracePingCli();
        var rootCommand = tracePingCli.CreateCommands();
        if (rootCommand == null)
        {
            return (1);
        }

        await rootCommand.InvokeAsync(args);
        return (0);
    }
}
