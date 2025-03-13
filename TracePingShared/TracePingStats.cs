using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Peamel.NetworkUtilities
{
    /// <summary>
    /// Class to keep track of stats specific to Trace Pings
    /// </summary>
    public class TracePingStats
    {
        #region General Properties
        const int MinDefaultValues = 99999999;

        /// <summary>
        /// The hop id. Represents the number of the hop.
        /// </summary>
        public int HopID { get; set; }

        /// <summary>
        /// The IP address.
        /// </summary>
        public string Address { get; set; }

        /// <summary>
        /// The hostname
        /// </summary>
        public string Hostname { get; set; }

        public IPStatus ReplyStatus { get; set; }
        #endregion General Properties

        #region Stats Properties 
        // ========   Stats   ========
        public Boolean CalcPercentile = false;

        public long TotalPings { get; set; } = 0;
        public long TotalLost { get; set; } = 0;

        public long RoundTripTime { get; set; } = 0;
        public long TotalRoundTripTime { get; set; } = 0;
        public long MinRoundTripTime { get; set; } = 99999999;
        public long MaxRoundTripTime { get; set; } = 0;
        public long LastRoundTripTime { get; set; } = 0;
        public long LastJitter { get; set; } = 0;

        public long TotalJitter { get; set; } = 0;
        public long MinJitter { get; set; } = 99999999;
        public long MaxJitter { get; set; } = 0;

        public DateTime LastUpdate { get; set; } = DateTime.MinValue;

        // List to keep track of individual Round Trip Time (RTT)
        // This is required to calculate percentiles
        List<long> RttList = new List<long>();

        // List to keep track of individual Jitter times
        // This is required to calculate percentiles
        List<long> JitterList = new List<long>();

        /// <summary>
        /// Calculates the average Jitter
        /// </summary>
        public double AvgJitter
        {
            get
            {
                long totalSuccessPings = TotalPings - TotalLost;
                if (totalSuccessPings < 1)
                    return 0;
                return (double)(TotalJitter / totalSuccessPings);
            }
        }

        /// <summary>
        /// Calculates the percentage of pings lost
        /// </summary>
        public double LostPercentage
        {
            get
            {
                if (TotalPings < 1)
                    return 0;

                double lossp = ((double)TotalLost / TotalPings) * 100;

                return lossp;
            }
        }

        // https://stackoverflow.com/questions/8137391/percentile-calculation
        /// <summary>
        /// Calculates the percentileb, based on the long values within the Enumerable
        /// </summary>
        /// <param name="seq"></param>
        /// <param name="percentile"></param>
        /// <returns></returns>
        public double Percentile(IEnumerable<long> seq, double percentile)
        {
            if (seq.Count() < 5)
                return 0;

            var elements = seq.ToArray();
            Array.Sort(elements);
            double realIndex = percentile * (elements.Length - 1);
            int index = (int)realIndex;
            double frac = realIndex - index;
            if (index + 1 < elements.Length)
                return elements[index] * (1 - frac) + elements[index + 1] * frac;
            else
                return elements[index];
        }

        /// <summary>
        /// Calculates the average round trip time
        /// </summary>
        /// <returns></returns>
        public double AvgRoundTripTime
        {
            get
            {
                long totalSuccessPings = TotalPings - TotalLost;
                if (totalSuccessPings < 1)
                    return 0;
                return (double)(TotalRoundTripTime / TotalPings);
            }
        }
        #endregion Stats Properties 

        #region Stats CRUD Operations
        /// <summary>
        /// Records when a ping has been lost, or exceeded the limit
        /// </summary>
        public void RecordLost()
        {
            TotalPings++;
            TotalLost++;
        }

        /// <summary>
        /// Clears statitics. Also sets very high default values
        /// for stats that keep track of minimum values
        /// </summary>
        public void Clear()
        {
            TotalPings = 0;
            TotalLost = 0;
            RoundTripTime = 0;
            TotalRoundTripTime = 0;
            MinRoundTripTime = MinDefaultValues;
            MaxRoundTripTime = 0;
            LastRoundTripTime = 0;
            LastJitter = 0;
            TotalJitter = 0;
            MinJitter = MinDefaultValues;
            MaxJitter = 0;
            if (RttList != null)
                RttList.Clear();
            if (JitterList != null)
                JitterList.Clear();
        }

        /// <summary>
        /// Used to insert the ping round trip time
        /// If stats are not configured for percentiles, it will NOT
        /// store the individual Round Trip Time (RTT) values
        /// </summary>
        /// <param name="rtt"></param>
        public void RecordRountTripTime(long rtt)
        {
            LastUpdate = DateTime.Now;

            TotalPings++;
            TotalRoundTripTime += rtt;

            if (MinRoundTripTime > rtt)
                MinRoundTripTime = rtt;

            if (MaxRoundTripTime < rtt)
                MaxRoundTripTime = rtt;

            long jitter = 0;
            jitter = LastRoundTripTime - rtt;
            if (jitter < 0)
                jitter = -jitter;

            if (MinJitter > jitter)
                MinJitter = jitter;

            if (MaxJitter < jitter)
                MaxJitter = jitter;

            TotalJitter += jitter;

            LastJitter = jitter;

            if (CalcPercentile == true)
            {
                RttList.Add(rtt);
                JitterList.Add(jitter);
            }

            LastRoundTripTime = rtt;
        }
        #endregion Stats CRUD Operations


        #region Displaying and Saving data

        // Define formating of the string
        // StringFormatConsole specifically leaves out 0, as we don't display the date/time in the row on the console
        static String StringFormatConsole = "{1,3} | {2, -16} | {3, 5} | {4, 4} | {5, 5} | {6, 4} | {7, 4} || {8, 6} | {9, 6} | {10, 6} | {11, 6} || {12, 6} | {13, 6} | {14, 6} | {15, 6}";
        static String StringFormatCsv = "\"{0}\",\"{1}\",\"{2}\",\"{3}\",\"{4}\",\"{5}\",\"{6}\",\"{7}\",\"{8}\",\"{9}\",\"{10}\",\"{11}\",\"{12}\",\"{13}\",\"{14}\",\"{15}\"";

        /// <summary>
        /// Returns the titles of the columns as a string, suitable for a console application
        /// </summary>
        /// <returns></returns>
        public static String HeaderToConsole()
        {
            return HeaderToString(StringFormatConsole);
        }

        /// <summary>
        /// Returns the titles of the columns as a string, in CSV format
        /// </summary>
        /// <returns></returns>
        public static String HeaderToCsv()
        {
            return HeaderToString(StringFormatCsv);
        }

        /// <summary>
        /// Returns the titles of the columns, based on the String Formatter
        /// </summary>
        /// <param name="stringFormat"></param>
        /// <returns></returns>
        private static String HeaderToString(String stringFormat)
        {
            String header = string.Format(stringFormat,
                "Date",
                "Hop",
                "Address",
                "Sent",
                "Lost",
                "Lost%",
                "RTT",
                "JTR",
                "P98 RT",
                "Min RT",
                "Max RT",
                "Avg RT",
                "P98 JT",
                "Min JT",
                "Max JT",
                "Avg JT"
                );
            return header;
        }

        /// <summary>
        /// Returns the stats, in a format suitable for a console
        /// </summary>
        /// <returns></returns>
        public string DataToConsole()
        {
            if (ReplyStatus == IPStatus.TimedOut)
            {
                return string.Format("{0,3} | {1, -16} | {2, 4}",
                    HopID,
                    string.IsNullOrEmpty(Hostname) ? Address : Hostname + "[" + Address + "]",
                    "Request timed out");
            }

            return DataToString(StringFormatConsole);
        }

        /// <summary>
        /// Returns the stats, in a CSV format
        /// </summary>
        /// <returns></returns>
        public string DataToCsv()
        {
            return DataToString(StringFormatCsv);
        }

        /// <summary>
        /// Returns the stats as a string, based on the String Formatter
        /// </summary>
        /// <param name="stringFormat"></param>
        /// <returns></returns>
        private string DataToString(String stringFormat)
        {
            String Rtt98PString = "N/A";
            String Jitter98PString = "N/A";

            if (CalcPercentile)
            {
                double RttPercentile98 = Percentile(RttList, 0.98);
                Rtt98PString = RttPercentile98.ToString("N0");

                double JitterPercentile98 = Percentile(JitterList, 0.98);
                Jitter98PString = JitterPercentile98.ToString("N0");
            }

            return string.Format(stringFormat,
                LastUpdate,
                HopID,
                string.IsNullOrEmpty(Hostname) ? Address : Hostname + "[" + Address + "]",
                TotalPings,
                TotalLost,
                LostPercentage.ToString("N0") + "%",
                LastRoundTripTime,
                LastJitter,
                Rtt98PString,
                MinRoundTripTime,
                MaxRoundTripTime,
                AvgRoundTripTime,
                Jitter98PString,
                MinJitter,
                MaxJitter,
                AvgJitter
                );
        }
        #endregion Displaying and Saving data
    }

}
