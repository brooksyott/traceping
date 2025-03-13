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
    /// Class to keep track of individual pings and the results
    /// </summary>
    public class TracePingResult
    {
        /// <summary>
        /// The hop id. Represents the number of the hop.
        /// </summary>
        public int HopID { get; set; }

        /// <summary>
        /// The IP address.
        /// </summary>
        public string Address { get; set; }

        public IPAddress FullAddress { get; set; }

        /// <summary>
        /// The hostname
        /// </summary>
        public string Hostname { get; set; }

        /// <summary>
        /// The reply time it took for the host to receive and reply to the request in milliseconds.
        /// </summary>
        public long ReplyTime { get; set; }

        /// <summary>
        /// The reply status of the request.
        /// </summary>
        public IPStatus ReplyStatus { get; set; }

        /// <summary>
        /// Override to pretty print the ping result
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            return string.Format("{0,3} | {1, -16} | {2, 4}",
                HopID,
                string.IsNullOrEmpty(Hostname) ? Address : Hostname + "[" + Address + "]",
                ReplyStatus == IPStatus.TimedOut ? "RTO" : ReplyTime.ToString()
                );
        }
    }

}
