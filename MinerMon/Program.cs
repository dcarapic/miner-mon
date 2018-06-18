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

        /// <summary>
        /// Defines the entry point of the application.
        /// </summary>
        /// <param name="args">The arguments.</param>
        static void Main(string[] args)
        {
            Console.WriteLine($"MinerMon v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
            try
            {
                // Sanity checking
                if (string.IsNullOrWhiteSpace(Properties.Settings.Default.PoolStatsAddressUrl))
                    throw new ArgumentException("PoolStatsAddressUrl parameter not set!");
                if (string.IsNullOrWhiteSpace(Properties.Settings.Default.MonitorName))
                    throw new ArgumentException("MonitorName parameter not set!");
                if (string.IsNullOrWhiteSpace(Properties.Settings.Default.StartMinerCommand))
                    throw new ArgumentException("StartMinerCommand parameter not set!");
                if (string.IsNullOrWhiteSpace(Properties.Settings.Default.StopMinerCommand))
                    throw new ArgumentException("StopMinerCommand parameter not set!");
                if (string.IsNullOrWhiteSpace(Properties.Settings.Default.MinerExecutable))
                    throw new ArgumentException("MinerExecutable parameter not set!");

                Console.WriteLine($"Press any key to exit.");

                // Initiate checking thread
                _cancelMonitor = new CancellationTokenSource();
                _monitorThread = new Thread(BackgroundThread);
                _monitorThread.IsBackground = true;
                _monitorThread.Start();

                // Monitor for key-press or the main thread exit
                while (!Console.KeyAvailable)
                {
                    if (_monitorThread.Join(100))
                    {
                        // Main thread exited - this only happens if there was an error, just exit.
                        return;
                    }
                }
                // User pressed a key - notify main thread, wait a bit for to exit (but not too much) and then terminate.
                _cancelMonitor.Cancel();
                Console.WriteLine($"Please wait, stopping monitoring.");
                _monitorThread.Join(TimeSpan.FromSeconds(10));

            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred while monitoring: {ex}");
            }
            finally
            {
                Console.WriteLine($"Exited.");
            }
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
                    Console.Write($"Checking if miner ('{Properties.Settings.Default.MinerExecutable}') is running .... ");

                    // Check miner process
                    var minerProc = GetMinerProcess();
                    if (minerProc == null && !_minerWasRunning)
                    {
                        Console.WriteLine($"not running (was not running before) - skipping check.");
                        continue;
                    }
                    else if(minerProc == null && _minerWasRunning)
                    {
                        Console.WriteLine($"not running (was running before) - restarting miner!");
                        Notify($"Miner executable not running, restarting miner!");
                        if (!RestartMiner())
                            return;
                        continue;
                    }
                    else
                    {
                        // Miner is working but skip checking if the miner was just started
                        _minerWasRunning = true;
                        var minerStarted = DateTime.Now.Subtract(minerProc.StartTime);
                        if (minerStarted < Properties.Settings.Default.PoolMaximumLastUpdateTimeout)
                        {
                            Console.WriteLine($"running but it was started {minerStarted.TotalSeconds:n0} seconds ago - skipping check.");
                            continue;
                        }
                        else
                        {
                            Console.WriteLine($"running.");
                        }
                    }

                    // Check the last update of the miner pool
                    Console.Write($"Checking the miner pool last update .... ");
                    var poolStatus = IsMinerActiveOnPool();
                    if (poolStatus == null)
                    {
                        // If the pool is down or no network just skip tge check
                        Console.WriteLine($"could not determine pool status - skipping check.");
                        continue;
                    }
                    else if (!poolStatus.Value )
                    {
                        Console.WriteLine($"pool is not updated in the last {Properties.Settings.Default.PoolMaximumLastUpdateTimeout.TotalSeconds:n0} seconds - restarting miner!");
                        Notify($"Miner pool not updating, restarting miner!");
                        if (!RestartMiner())
                            return;
                        continue;
                    }
                    else
                    {
                        Console.WriteLine($"miner pool is updated - all OK.");
                    }
                } // now wait one minute (or until canceled)
                while (!token.WaitHandle.WaitOne(TimeSpan.FromMinutes(1)));

            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred while monitoring: {ex}");
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
            if(Debugger.IsAttached)
            {
                Console.WriteLine($"Debugger attached, skipping email: {emailSubject}");
                return;
            }
            Console.WriteLine($"Sending email: {emailSubject}");
            try
            {
                if (string.IsNullOrWhiteSpace(Properties.Settings.Default.SMTPServer))
                {
                    Console.WriteLine("- SMTP server not defined, email not sent!");
                    return;
                }
                if (string.IsNullOrWhiteSpace(Properties.Settings.Default.EmailRecipients))
                {
                    Console.WriteLine("- Email recipients not defined, email not sent!");
                    return;
                }
                if (string.IsNullOrWhiteSpace(Properties.Settings.Default.EmailSender))
                {
                    Console.WriteLine("- Email sender not defined, email not sent!");
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
                var sending = client.SendMailAsync(msg);
                sending.Wait(_cancelMonitor.Token);

            }
            catch(TaskCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"- Email sending failed: {ex}");
            }
        }

        /// <summary>
        /// Restart the miner.
        /// </summary>
        /// <returns>True if the restart was successful and the miner is working.</returns>
        static bool RestartMiner()
        {
            // Immediately check if we need to exit
            if (_cancelMonitor.IsCancellationRequested)
                return false;

            Console.WriteLine($"Stopping miner '{Properties.Settings.Default.StopMinerCommand}'");
            var proc = Process.Start("cmd.exe", $"/c {Properties.Settings.Default.StopMinerCommand}");
            proc.WaitForExit();

            Console.WriteLine($"Starting miner '{Properties.Settings.Default.StartMinerCommand}'");
            Process.Start("cmd.exe", $"/c {Properties.Settings.Default.StartMinerCommand}");

            try
            {
                // Give miner 5 seconds to start-up
                Task.Delay(5000).Wait(_cancelMonitor.Token);
            }
            catch (TaskCanceledException)
            {
                // If we were canceled then just exit
                return false;
            }
            if(GetMinerProcess() == null)
            {
                Notify("Failed to restart miner executable ... stopping monitoring!");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Gets the miner process. 
        /// </summary>
        static Process GetMinerProcess()
        {
            foreach(var proc in Process.GetProcesses())
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
                Console.WriteLine($"- Could not query miner pool for stats: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Converts unix time stamp to date time.
        /// </summary>
        /// <param name="unixTimeStamp">The unix time stamp.</param>
        /// <returns></returns>
        public static DateTime UnixTimeStampToDateTime(double unixTimeStamp)
        {
            // Unix timestamp is seconds past epoch
            System.DateTime dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
            dtDateTime = dtDateTime.AddSeconds(unixTimeStamp).ToLocalTime();
            return dtDateTime;
        }
    }
}
