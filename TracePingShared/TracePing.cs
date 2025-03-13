using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Peamel.NetworkUtilities
{
    /// <summary>
    /// Class to implement a Trace Ping functionality
    /// A trace ping first finds all the routes to a host
    /// and the runs pings to each hop to that host
    /// The initial tracing of the route is done using Pings, 
    /// with a Time To Live (TTL - or number of hops) such that
    /// the ping terminates and returns the IP of the host that returned
    /// the ping. This maps the route to the end host.
    /// </summary>
    public class TracePing
    {
        #region Properties / Fields

        const string NO_HOSTNAME = "*.*.*.*";

        // Sets if we are to get the host name via DNS
        // This will substantially slow down the initial 
        // discovery of the routes
        public Boolean GetHostName { get; set; } = true;

        // Determines if we want to calculate percentiles
        // If so, each ping for each host is captured
        // This will take a lot more memory
        public Boolean CalcPercentile { get; set; } = true;

        // Specific behaviour configuration, and defaults
        private int _maxHops = 50;
        private int _discoveryTimeout = 3000;
        private int _pingTimeout = 1000;
        private IPAddress _address = null;

        // Cancel token used to stop the pinging of the hosts on the route
        CancellationTokenSource PingRoutesCts = new CancellationTokenSource();

        // Lock object to help with multithreading
        Object lockObject = new object();

        // Set on the initial route dicovery
        // Each index into the array is the hop to
        // one of the hosts on the route to the end/target host
        TracePingResult[] _routes;

        // We use to sets of ping stats
        // so that the console can display stats
        // independantly of what is recorded in the
        // CSV file

        // Ping stats for the console
        TracePingStats[] _pingStatsConsole = null;

        // Ping stats for CSV 
        TracePingStats[] _pingStatsCsv = null;
        #endregion Properties / Fields

        #region Event Handling
        /// <summary>
        /// Event handler definition for when the ping of all hosts 
        /// in the path has completed
        /// </summary>
        public event EventHandler PingCompleteEvent;

        /// <summary>
        /// Notifies interested parties bound to PingCompleteEvent
        /// that all pings have completed for all hosts in the route
        /// </summary>
        /// <param name="e"></param>
        protected virtual void NotifyPingComplete(EventArgs e)
        {
            EventHandler ev = PingCompleteEvent;
            PingCompleteEvent?.Invoke(this, e);
        }
        #endregion Event Handling

        /// <summary>
        /// Constructor. Sets up the configuration to control the 
        /// trace route and ping behaviour
        /// </summary>
        /// <param name="hostAddress"></param>
        /// <param name="maxHops"></param>
        /// <param name="discoveryTimeout"></param>
        /// <param name="pingTimeout"></param>
        public TracePing(String hostAddress, int maxHops, int discoveryTimeout, int pingTimeout)
        {
            if (maxHops < 1)
                throw new ArgumentException("Max hops can't be lower than 1.");

            _maxHops = maxHops;
            _discoveryTimeout = discoveryTimeout;
            _pingTimeout = pingTimeout;

            // Ensure that the argument address is valid.
            if (!IPAddress.TryParse(hostAddress, out _address))
            {
                // Could be a host name
                IPAddress[] ipaddressArray = Dns.GetHostAddresses(hostAddress);
                if (ipaddressArray.Length <= 0)
                {
                    throw new ArgumentException(string.Format("{0} is not a valid address.", hostAddress));
                }
                _address = ipaddressArray[0];
            }
        }

        /// <summary>
        /// Stop the ping requests
        /// </summary>
        public void PingRoutesStop()
        {
            PingRoutesCts.Cancel();
        }

        /// <summary>
        /// Reset the cancel token to restart the ping requests
        /// </summary>
        public void PingRoutesReset()
        {
            PingRoutesCts.Cancel();
            PingRoutesCts = null;
            PingRoutesCts = new CancellationTokenSource();
        }

        /// <summary>
        /// Return the console view of ping results
        /// </summary>
        /// <returns></returns>
        public TracePingStats[] LastPingResultConsole()
        {
            // Lock the stats when we are manipulating them
            lock (lockObject)
            {
                var lastPingResult = _pingStatsConsole;
                return lastPingResult;
            }
        }

        /// <summary>
        /// Return the CSV view of ping results
        /// </summary>
        /// <returns></returns>
        public TracePingStats[] LastPingResultCsv()
        {
            // Lock the stats when we are manipulating them
            lock (lockObject)
            {
                var lastPingResult = _pingStatsCsv;
                return lastPingResult;
            }
        }

        /// <summary>
        /// Clears the console stats
        /// </summary>
        public void ClearStats()
        {
            // Lock the stats when we are manipulating them
            lock (lockObject)
            {
                foreach (TracePingStats r in _pingStatsConsole)
                {
                    r.Clear();
                }
            }
        }

        /// <summary>
        /// Clears the CSV stats
        /// </summary>
        public void ClearStatsCsv()
        {
            // Lock the stats when we are manipulating them
            lock (lockObject)
            {
                foreach (TracePingStats r in _pingStatsCsv)
                {
                    r.Clear();
                }
            }
        }

        byte[] CreateIcmpPacket()
        {
            byte[] packet = new byte[8 + 32];
            packet[0] = 8; // Type: Echo Request
            packet[1] = 0; // Code: 0
            packet[2] = 0; // Checksum (computed later)
            packet[3] = 0;
            packet[4] = 0; // Identifier
            packet[5] = 1;
            packet[6] = 0; // Sequence Number
            packet[7] = 1;
            // Add some data to the packet (optional)
            for (int i = 8; i < packet.Length; i++)
            {
                packet[i] = (byte)'A';
            }

            ushort checksum = ComputeChecksum(packet);
            packet[2] = (byte)(checksum >> 8);
            packet[3] = (byte)(checksum & 0xFF);

            return packet;
        }

        ushort ComputeChecksum(byte[] buffer)
        {
            int sum = 0;
            for (int i = 0; i < buffer.Length; i += 2)
            {
                ushort word = (ushort)((buffer[i] << 8) + (i + 1 < buffer.Length ? buffer[i + 1] : 0));
                sum += word;
            }

            while ((sum >> 16) > 0)
            {
                sum = (sum & 0xFFFF) + (sum >> 16);
            }

            return (ushort)~sum;
        }

        public void GetRoutesUDP()
        {
            try
            {
                IPEndPoint endPoint = new IPEndPoint(_address, 0);
                int port = 33434; // Starting port number for UDP traceroute
                Console.WriteLine($"\nTracing route to {_address} [{_address}] over a maximum of {_maxHops} hops:\n");

                using (Socket udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                using (Socket icmpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp))
                {
                    icmpSocket.Bind(new IPEndPoint(IPAddress.Any, 0)); // Bind the ICMP socket to receive responses
                    icmpSocket.ReceiveTimeout = 10000; // Increase receive timeout to 10 seconds

                    for (int ttl = 1; ttl <= _maxHops; ttl++)
                    {
                        udpSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, ttl);
                        IPEndPoint destinationEndPoint = new IPEndPoint(_address, port);
                        EndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

                        byte[] sendBuffer = Encoding.ASCII.GetBytes("Traceroute UDP Test");
                        byte[] receiveBuffer = new byte[1024];

                        Stopwatch stopwatch = Stopwatch.StartNew();
                        udpSocket.SendTo(sendBuffer, destinationEndPoint);

                        try
                        {
                            int bytesReceived = icmpSocket.ReceiveFrom(receiveBuffer, ref remoteEndPoint);
                            stopwatch.Stop();

                            if (bytesReceived > 0)
                            {
                                IPAddress responderIP = ((IPEndPoint)remoteEndPoint).Address;
                                Console.WriteLine($"{ttl,2}  {responderIP}  {stopwatch.ElapsedMilliseconds} ms");

                                if (responderIP.Equals(_address))
                                {
                                    Console.WriteLine("\nTrace complete.");
                                    break;
                                }
                            }
                            else
                            {
                                Console.WriteLine($"{ttl,2}  *  (No response)");
                            }
                        }
                        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                        {
                            Console.WriteLine($"{ttl,2}  *  (Timeout, {ex.Message})");
                        }
                        catch (SocketException ex)
                        {
                            Console.WriteLine($"Error: {ex.Message}");
                        }

                        port++; // Increment port to avoid issues with multiple hops
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }



        public void GetRoutesICMP()
        {
            try
            {
                IPEndPoint endPoint = new IPEndPoint(_address, 0);

                Console.WriteLine($"\nTracing route to {_address} [{_address}] over a maximum of {_maxHops} hops:\n");

                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp))
                {
                    socket.ReceiveTimeout = 10000; // Increase receive timeout to 10 seconds

                    for (int ttl = 1; ttl <= _maxHops; ttl++)
                    {
                        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, ttl);

                        byte[] icmpPacket = CreateIcmpPacket();
                        EndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
                        Stopwatch stopwatch = Stopwatch.StartNew();

                        try
                        {
                            socket.SendTo(icmpPacket, endPoint);
                            byte[] buffer = new byte[1024];
                            int bytesReceived = socket.ReceiveFrom(buffer, ref remoteEndPoint);
                            stopwatch.Stop();

                            if (bytesReceived > 0)
                            {
                                IPAddress responderIP = ((IPEndPoint)remoteEndPoint).Address;
                                Console.WriteLine($"{ttl,2}  {responderIP}  {stopwatch.ElapsedMilliseconds} ms");

                                if (responderIP.Equals(_address))
                                {
                                    Console.WriteLine("\nTrace complete.");
                                    break;
                                }
                            }
                            else
                            {
                                Console.WriteLine($"{ttl,2}  *  (No response)");
                            }
                        }
                        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                        {
                            Console.WriteLine($"{ttl,2}  *  (Timeout, {ex.Message})");
                        }
                        catch (SocketException ex)
                        {
                            Console.WriteLine($"Error: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }

        public TracePingResult[] GetRoutes()
        {
            Ping ping = new Ping();
            Task<TracePingResult>[] pingArray = new Task<TracePingResult>[_maxHops];
            PingOptions options = new PingOptions();
            options.DontFragment = true;
            byte[] buffer = Encoding.ASCII.GetBytes("Test Data");
            TracePingResult[] pingResults = new TracePingResult[_maxHops];

            for (int ttl = 1; ttl <= _maxHops; ttl++)
            {
                options.Ttl = ttl;
                PingReply reply = ping.Send(_address, 10000, buffer, options);

                Console.Write($"{ttl,2}  "); // Hop number formatting

                if (reply.Status == IPStatus.TtlExpired || reply.Status == IPStatus.Success)
                {
                    Console.WriteLine($"{reply.Address} : Replied");
                    pingResults[ttl-1] = new TracePingResult()
                    {
                        HopID = ttl,
                        Address = reply.Address.ToString(),
                        Hostname = "",
                        FullAddress = _address,
                        ReplyTime = 0,
                        ReplyStatus = reply.Status
                    };
                }
                else
                {
                    Console.WriteLine($"* : {reply.Status}");
                    pingResults[ttl - 1] = new TracePingResult()
                    {
                        HopID = ttl,
                        Address = NO_HOSTNAME,
                        Hostname = NO_HOSTNAME,
                        FullAddress = _address,
                        ReplyTime = 0,
                        ReplyStatus = reply.Status
                    };
                }

                if (reply.Status == IPStatus.Success)
                {
                    Console.WriteLine("\nTrace complete.");
                    Array.Resize(ref pingResults, ttl);
                    break;
                }
            }

            _routes = pingResults;
            _pingStatsConsole = null;
            _pingStatsConsole = new TracePingStats[pingResults.Length];

            _pingStatsCsv = null;
            _pingStatsCsv = new TracePingStats[pingResults.Length];

            // Initialize the stats
            foreach (TracePingResult trp in pingResults)
            {
                TracePingStats pingStatConsole = new TracePingStats();
                pingStatConsole.Address = trp.Address;
                pingStatConsole.HopID = trp.HopID;
                pingStatConsole.Hostname = trp.Hostname;
                pingStatConsole.ReplyStatus = trp.ReplyStatus;
                _pingStatsConsole[trp.HopID - 1] = pingStatConsole;
                _pingStatsConsole[trp.HopID - 1].CalcPercentile = CalcPercentile;

                TracePingStats pingStatCsv = new TracePingStats();
                pingStatCsv.Address = trp.Address;
                pingStatCsv.HopID = trp.HopID;
                pingStatCsv.Hostname = trp.Hostname;
                pingStatCsv.ReplyStatus = trp.ReplyStatus;
                _pingStatsCsv[trp.HopID - 1] = pingStatCsv;
                _pingStatsCsv[trp.HopID - 1].CalcPercentile = CalcPercentile;
            }

            return pingResults;
        }


        /// <summary>
        /// Pings each host discovered by GetRoutesAsync continously
        /// Ping frequency determines the delay between pings
        /// The cancel token is used to stop pinging, and 
        /// is set by calling PingRoutesStop()
        /// </summary>
        /// <param name="pingFrequency"></param>
        /// <returns></returns>
        public async Task PingRoutesContinous(int pingFrequency)
        {
            while (!PingRoutesCts.IsCancellationRequested)
            {
                TracePingResult[] pingResults = new TracePingResult[_routes.Length];
                pingResults = await PingRoutes();
                lock (lockObject)
                {
                    foreach (TracePingResult r in pingResults)
                    {
                        if ((r.ReplyStatus == IPStatus.Success) || (r.ReplyStatus == IPStatus.TtlExpired))
                        {
                            _pingStatsConsole[r.HopID - 1].RecordRountTripTime(r.ReplyTime);
                            _pingStatsCsv[r.HopID - 1].RecordRountTripTime(r.ReplyTime);
                        }
                        else
                        {
                            _pingStatsConsole[r.HopID - 1].RecordLost();
                            _pingStatsCsv[r.HopID - 1].RecordLost();
                        }
                    }
                }
                NotifyPingComplete(null);
                await Task.Delay(pingFrequency);
            }
        }

        /// <summary>
        /// each host discovered by GetRoutesAsync 
        /// </summary>
        /// <returns></returns>
        private async Task<TracePingResult[]> PingRoutes()
        {
            Task<TracePingResult>[] pingArray = new Task<TracePingResult>[_routes.Length];

            foreach (TracePingResult tpr in _routes)
            {
                int idx = tpr.HopID - 1;
                pingArray[idx] = Task.Run(() => Ping(tpr, tpr.HopID, _pingTimeout));
            }

            TracePingResult[] pingResults = await Task.WhenAll(pingArray).ConfigureAwait(false);

            return pingResults;
        }

        /// <summary>
        /// Pings and individual host
        /// TODO: Clean this up a bit 
        /// </summary>
        /// <param name="tpr"></param>
        /// <param name="hop"></param>
        /// <param name="timeout"></param>
        /// <returns></returns>
        private TracePingResult Ping(TracePingResult tpr, int hop, int timeout)
        {
            // If the address/did not reply, do nothing
            if (tpr.Address == NO_HOSTNAME)
            {
                // Do nothing
                return new TracePingResult()
                {
                    HopID = tpr.HopID,
                    Address = tpr.Address,
                    Hostname = tpr.Hostname,
                    FullAddress = tpr.FullAddress,
                    ReplyTime = 0,
                    ReplyStatus = tpr.ReplyStatus
                };
            }

            Ping ping = new Ping();
            PingOptions pingOptions = new PingOptions(hop, true);
            Stopwatch pingReplyTime = new Stopwatch();
            PingReply reply;

            pingReplyTime.Start();
            reply = ping.Send(tpr.FullAddress, timeout, new byte[] { 0 }, pingOptions);
            pingReplyTime.Stop();

            string hostname = string.Empty;
            if ((reply.Address != null) && (GetHostName == true))
            {
                try
                {
                    hostname = Dns.GetHostEntry(reply.Address).HostName;    // Retrieve the hostname for the replied address.
                }
                catch (Exception)
                {
                    /* No host available for that address. */
                }
            }

            return new TracePingResult()
            {
                HopID = pingOptions.Ttl,
                Address = ((reply.Address == null) || (reply.Address.ToString() == "0.0.0.0")) ? NO_HOSTNAME : reply.Address.ToString(),
                Hostname = hostname,
                FullAddress = tpr.FullAddress,
                ReplyTime = pingReplyTime.ElapsedMilliseconds,
                ReplyStatus = reply.Status
            };
        }

        /// <summary>
        /// Ping based on the ipAddress
        /// </summary>
        /// <param name="ipAddress"></param>
        /// <param name="hop"></param>
        /// <param name="timeout"></param>
        /// <returns></returns>
        private TracePingResult Ping(IPAddress ipAddress, int hop, int timeout)
        {

            Ping ping = new Ping();
            PingOptions pingOptions = new PingOptions(hop, true);
            Stopwatch pingReplyTime = new Stopwatch();
            PingReply reply;

            pingReplyTime.Start();
            reply = ping.Send(ipAddress, timeout, new byte[] { 0 }, pingOptions);
            pingReplyTime.Stop();
            Console.WriteLine($"Reply: {reply}");

            string hostname = string.Empty;
            if ((reply.Address != null) && (GetHostName == true))
            {
                try
                {
                    hostname = Dns.GetHostEntry(reply.Address).HostName;    // Retrieve the hostname for the replied address.
                }
                catch (Exception)
                {
                    /* No host available for that address. */
                }
            }


            return new TracePingResult()
            {
                HopID = pingOptions.Ttl,
                //Address = ((reply.Status == IPStatus.TimedOut) || (reply.Address.ToString() == "0.0.0.0")) ? NO_HOSTNAME : reply.Address.ToString(),
                Address = (reply.Status == IPStatus.TimedOut) ? NO_HOSTNAME : reply.Address.ToString(),
                Hostname = hostname,
                FullAddress = ipAddress,
                ReplyTime = pingReplyTime.ElapsedMilliseconds,
                ReplyStatus = reply.Status
            };
        }

        /// <summary>
        /// Not used, but a nice example of a trace route function using yield
        /// so the caller can use "foreach"
        /// </summary>
        /// <param name="hostAddress"></param>
        /// <param name="maxHops"></param>
        /// <param name="timeout"></param>
        /// <returns></returns>
        public IEnumerable<TracePingResult> TraceRt(string hostAddress, int maxHops, int timeout)
        {
            IPAddress address;

            // Ensure that the argument address is valid.
            if (!IPAddress.TryParse(hostAddress, out address))
            {
                // Could be a host name
                IPAddress[] ipaddressArray = Dns.GetHostAddresses(hostAddress);
                if (ipaddressArray.Length <= 0)
                {
                    throw new ArgumentException(string.Format("{0} is not a valid address.", hostAddress));
                }
                address = ipaddressArray[0];
            }

            // Max hops should be at least one or else there won't be any data to return.
            if (maxHops < 1)
                throw new ArgumentException("Max hops can't be lower than 1.");

            // Ensure that the timeout is not set to 0 or a negative number.
            if (timeout < 1)
                throw new ArgumentException("Timeout value must be higher than 0.");


            Ping ping = new Ping();
            PingOptions pingOptions = new PingOptions(1, true);
            Stopwatch pingReplyTime = new Stopwatch();
            PingReply reply;

            do
            {
                pingReplyTime.Start();
                reply = ping.Send(address, timeout, new byte[] { 0 }, pingOptions);
                pingReplyTime.Stop();

                string hostname = string.Empty;
                if ((reply.Address != null) && (GetHostName == true))
                {
                    try
                    {
                        hostname = Dns.GetHostEntry(reply.Address).HostName;    // Retrieve the hostname for the replied address.
                    }
                    catch (Exception)
                    {
                        /* No host available for that address. */
                    }
                }

                // Return out TracertEntry object with all the information about the hop.
                yield return new TracePingResult()
                {
                    HopID = pingOptions.Ttl,
                    Address = ((reply.Address == null) || (reply.Address.ToString() == "0.0.0.0")) ? "N/A" : reply.Address.ToString(),
                    Hostname = hostname,
                    ReplyTime = pingReplyTime.ElapsedMilliseconds,
                    ReplyStatus = reply.Status
                };

                pingOptions.Ttl++;
                pingReplyTime.Reset();
            }
            while (reply.Status != IPStatus.Success && pingOptions.Ttl <= maxHops);
        }
    }
}
