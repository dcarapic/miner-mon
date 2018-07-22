using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MinerMon
{
    public class Program
    {

        // Detection of miner state is done in this thread
        private static Thread _monitorThread;

        // When user presses ant button to stop the application then the cancelation token is raised so that the monitor thread can check it an exit.
        private static CancellationTokenSource _cancelMonitor;

        // Set once the miner is running at least one time
        private static bool _minerWasRunning;

        // How often the check is performed
        private static TimeSpan _checkPeriod = Debugger.IsAttached ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(1);

        private static object _lock = new object();

        /// <summary>
        /// Defines the entry point of the application.
        /// </summary>
        /// <param name="args">The arguments.</param>
        static void Main(string[] args)
        {
            LogLine($"MinerMon v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
            try
            {
                // Sanity checking
                if (string.IsNullOrWhiteSpace(Properties.Settings.Default.PoolStatsAddressUrl))
                    LogLine($"PoolStatsAddressUrl not set in configuration file, ignoring pool monitoring!");
                if (string.IsNullOrWhiteSpace(Properties.Settings.Default.MonitorName))
                    throw new ArgumentException("MonitorName parameter not set!");
                if (string.IsNullOrWhiteSpace(Properties.Settings.Default.StartMinerCommand))
                    throw new ArgumentException("StartMinerCommand parameter not set!");
                if (string.IsNullOrWhiteSpace(Properties.Settings.Default.StopMinerCommand))
                    throw new ArgumentException("StopMinerCommand parameter not set!");
                if (string.IsNullOrWhiteSpace(Properties.Settings.Default.MinerExecutable))
                    throw new ArgumentException("MinerExecutable parameter not set!");

                PrintInfo();

                // Initiate checking thread
                _cancelMonitor = new CancellationTokenSource();
                _monitorThread = new Thread(BackgroundThread);
                _monitorThread.IsBackground = true;
                _monitorThread.Start();

                // Monitor for key-press or the main thread exit
                ConsoleKey key;
                while(true)
                {
                    if (Console.KeyAvailable)
                    {
                        key = Console.ReadKey().Key;
                        if (key == ConsoleKey.K)
                        {
                            lock(_lock)
                            {
                                StopMiner();
                            }
                        }
                        else if (key == ConsoleKey.R)
                        {
                            lock (_lock)
                            {
                                if (!StopMiner())
                                    continue;
                                StartMiner();
                            }
                        }
                        else if (key == ConsoleKey.X)
                        {
                            break;
                        }
                        else
                        {
                            PrintInfo();
                        }
                    }
                    else
                    {
                        if (_monitorThread.Join(100))
                        {
                            // Main thread exited - this only happens if there was an error, just exit.
                            return;
                        }
                    }

                }

                // User pressed a key - notify main thread, wait a bit for to exit (but not too much) and then terminate.
                _cancelMonitor.Cancel();
                LogLine($"Please wait, stopping monitoring.");
                _monitorThread.Join(TimeSpan.FromSeconds(10));


            }
            catch (Exception ex)
            {
                LogLine($"An error occurred while monitoring: {ex}");
            }
            finally
            {
                LogLine($"Exited.");
            }
        }

        static void PrintInfo()
        {
            LogLine($"");
            LogLine($"Press key to execute:");
            LogLine($"K - Force kill miner");
            LogLine($"R - Restart miner");
            LogLine($"X - Exit monitor miner (does not kill miner process)");
            LogLine($"");
        }

        static void BackgroundThread(object ignored)
        {
            // Immediately check if we need to exit
            if (_cancelMonitor.IsCancellationRequested)
                return;
            try
            {
                if (Properties.Settings.Default.NotifyOnStartup)
                    Notify($"Started");

                var token = _cancelMonitor.Token;
                do
                {
                    lock (_lock)
                    {
                        LogLine($"Checking if miner ('{Properties.Settings.Default.MinerExecutable}') is running ... ");

                        // Check miner process
                        var minerProc = GetMinerProcess();
                        if (minerProc == null && !_minerWasRunning)
                        {
                            LogLine($"  ... is not running (was not running before) - skipping check.");
                            continue;
                        }
                        else if (minerProc == null && _minerWasRunning)
                        {
                            LogLine($"  ... is not running (was running before) - restarting miner!");
                            if (StartMiner())
                            {
                                Notify($"Miner executable was not running, miner started.");
                                continue;
                            }
                            else
                            {
                                Notify($"Miner executable was not running, failed to restart miner!");
                                return;
                            }
                        }
                        else
                        {
                            // Miner is working but skip checking if the miner was just started
                            var minerStarted = DateTime.Now.Subtract(minerProc.StartTime);
                            if (minerStarted < Properties.Settings.Default.PoolMaximumLastUpdateTimeout)
                            {
                                LogLine($"  ... is running but it was started {minerStarted.TotalSeconds:n0} seconds ago - skipping check.");
                                continue;
                            }
                            else
                            {
                                _minerWasRunning = true;
                                LogLine($"  ... running.");
                            }
                        }

                        // Check the last update of the miner pool
                        LogLine($"Checking the miner pool last update ... ");
                        var poolStatus = IsMinerActiveOnPool();
                        if (poolStatus == null)
                        {
                            // If the pool is down or no network just skip tge check
                            LogLine($"  ... could not determine pool status - skipping check.");
                            continue;
                        }
                        else if (!poolStatus.Value)
                        {
                            LogLine($"  ... pool is not updated in the last {Properties.Settings.Default.PoolMaximumLastUpdateTimeout.TotalSeconds:n0} seconds - restarting miner!");
                            if (!StopMiner())
                            {
                                Notify($"Miner pool not updating, failed to stop miner!");
                                return;
                            }
                            if (StartMiner())
                            {
                                Notify($"Miner pool not updating, miner re-started.");
                                continue;
                            }
                            else
                            {
                                Notify($"Miner pool not updating, failed to restart miner!");
                                return;
                            }
                        }
                        else
                        {
                            LogLine($"  ... miner pool is updated - all OK.");
                        }
                    }
                } // now wait one minute (or until canceled)
                while (!token.WaitHandle.WaitOne(_checkPeriod));

            }
            catch (Exception ex)
            {
                LogLine($"An error occurred while monitoring: {ex}");
            }

        }

        /// <summary>
        /// Notify by email.
        /// </summary>
        /// <param name="subject"></param>
        static void Notify(string subject)
        {
            // Immediately check if we need to exit
            if (_cancelMonitor.IsCancellationRequested)
                return;

            var emailSubject = $"MinerMon - {Properties.Settings.Default.MonitorName} - {subject}";
            if (Debugger.IsAttached)
            {
                LogLine($"Debugger attached, skipping email: {emailSubject}");
                return;
            }
            LogLine($"Sending email: {emailSubject}");
            try
            {
                if (string.IsNullOrWhiteSpace(Properties.Settings.Default.SMTPServer))
                {
                    LogLine("- SMTP server not defined, email not sent!");
                    return;
                }
                if (string.IsNullOrWhiteSpace(Properties.Settings.Default.EmailRecipients))
                {
                    LogLine("- Email recipients not defined, email not sent!");
                    return;
                }
                if (string.IsNullOrWhiteSpace(Properties.Settings.Default.EmailSender))
                {
                    LogLine("- Email sender not defined, email not sent!");
                    return;
                }
                SmtpClient client = new SmtpClient(Properties.Settings.Default.SMTPServer, Properties.Settings.Default.SMTPPort);
                MailMessage msg = new MailMessage();
                msg.From = new MailAddress(Properties.Settings.Default.EmailSender);
                foreach (var email in Properties.Settings.Default.EmailRecipients.Split(new string[] { ";" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    msg.To.Add(new MailAddress(email));
                }
                msg.Subject = emailSubject;
                client.Send(msg);
                client.Dispose();
            }
            catch (TaskCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                LogLine($"- Email sending failed: {ex}");
            }
        }

        /// <summary>
        /// Restart the miner.
        /// </summary>
        /// <returns>True if the restart was successful and the miner is working.</returns>
        static bool StartMiner()
        {
            LogLine($"Starting miner '{Properties.Settings.Default.StartMinerCommand}' ...");
            Process.Start("cmd.exe", $"/c {Properties.Settings.Default.StartMinerCommand}");

            try
            {
                // Give miner 10 seconds to start-up
                Task.Delay(10000).Wait(_cancelMonitor.Token);
            }
            catch (TaskCanceledException)
            {
                // If we were canceled then just exit
                return false;
            }
            if (GetMinerProcess() == null)
            {
                LogLine($"  ... failed - stopping monitoring!");
                return false;
            }
            LogLine($"  ... started.");
            return true;
        }


        /// <summary>
        /// Restart the miner.
        /// </summary>
        /// <returns>True if the restart was successful and the miner is working.</returns>
        static bool StopMiner()
        {
            // Immediately check if we need to exit
            if (_cancelMonitor.IsCancellationRequested)
                return false;

            LogLine($"Stopping miner '{Properties.Settings.Default.StopMinerCommand}' ... ");
            if (GetMinerProcess() == null)
            {
                LogLine($"  ... miner not started!");
                return true;
            }


            var proc = Process.Start("cmd.exe", $"/c {Properties.Settings.Default.StopMinerCommand}");
            proc.WaitForExit(5000);

            if (_cancelMonitor.IsCancellationRequested)
                return false;

            try
            {
                // wait 10 seconds
                Task.Delay(10000).Wait(_cancelMonitor.Token);
            }
            catch (TaskCanceledException)
            {
                // If we were canceled then just exit
                return false;
            }

            if (_cancelMonitor.IsCancellationRequested)
                return false;

            if (GetMinerProcess() != null)
            {
                LogLine($"  ... failed - stopping monitoring!");
                return false;
            }
            LogLine($"  ... stopped.");
            return true;
        }

        /// <summary>
        /// Gets the miner process. 
        /// </summary>
        static Process GetMinerProcess()
        {
            foreach (var proc in Process.GetProcesses())
            {
                // Immediately check if we need to exit
                if (_cancelMonitor.IsCancellationRequested)
                    return null;

                try
                {
                    if (string.Compare(proc.MainModule.ModuleName, Properties.Settings.Default.MinerExecutable, ignoreCase: true) == 0)
                        return proc;
                }
                catch { } // ignore if we can not access the executable
            }
            return null;
        }

        /// <summary>
        /// Determines whether the miner is active on the pool.
        /// </summary>
        /// <returns></returns>
        static bool? IsMinerActiveOnPool()
        {
            // Immediately check if we need to exit
            if (_cancelMonitor.IsCancellationRequested)
                return null;
            if (string.IsNullOrEmpty(Properties.Settings.Default.PoolStatsAddressUrl))
                return true;

            try
            {
                // Download JSON from the miner pool
                WebClient client = new WebClient();
                Task<string> download = client.DownloadStringTaskAsync(Properties.Settings.Default.PoolStatsAddressUrl);
                download.Wait(_cancelMonitor.Token);

                // Parse and check the lastShare of the miner
                var json = JObject.Parse(download.Result);
                var lastShareTxt = json["stats"]["lastShare"].ToString();
                var lastShare = UnixTimeStampToDateTime(long.Parse(lastShareTxt));
                return DateTime.Now.Subtract(lastShare) < Properties.Settings.Default.PoolMaximumLastUpdateTimeout;
            }
            catch (TaskCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                LogLine($"- Could not query miner pool for stats: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Converts unix time stamp to date time.
        /// </summary>
        /// <param name="unixTimeStamp">The unix time stamp.</param>
        /// <returns></returns>
        static DateTime UnixTimeStampToDateTime(double unixTimeStamp)
        {
            // Unix timestamp is seconds past epoch
            System.DateTime dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
            dtDateTime = dtDateTime.AddSeconds(unixTimeStamp).ToLocalTime();
            return dtDateTime;
        }

        static void LogLine(string text)
        {
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {text}");
        }


    }
}
