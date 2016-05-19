﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using Lithnet.Miiserver.Client;
using Lithnet.Logging;
using System.IO;
using System.Management.Automation;
using System.Collections.ObjectModel;
using System.Timers;

namespace Lithnet.Miiserver.AutoSync
{
    public static class Program
    {
        private static List<MAExecutor> maExecutors = new List<MAExecutor>();

        private static Timer runHistoryCleanupTimer;

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        public static void Main(string[] args)
        {
            bool runService = args != null && args.Contains("/service");

            if (!runService)
            {
                Lithnet.Logging.Logger.OutputToConsole = true;
            }

            if (runService)
            {
                ServiceBase[] servicesToRun = new ServiceBase[]
                {
                    new AutoSyncService()
                };

                ServiceBase.Run(servicesToRun);
            }
            else
            {
                Start();
                System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
            }
        }

        internal static void Start()
        {
            if (Settings.RunHistoryNumberOfDaysToKeep > 0)
            {
                Logger.WriteLine("Run history auto-cleanup enabled");
                Program.runHistoryCleanupTimer = new Timer
                {
                    AutoReset = true
                };

                Program.runHistoryCleanupTimer.Elapsed += RunHistoryCleanupTimer_Elapsed;
                Program.runHistoryCleanupTimer.Interval = TimeSpan.FromHours(8).TotalMilliseconds;
                Program.runHistoryCleanupTimer.Start();
                Program.ClearRunHistory();
            }

            EnumerateMAs();
        }

        private static void RunHistoryCleanupTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            Program.ClearRunHistory();
        }

        private static void ClearRunHistory()
        {
            if (Settings.RunHistoryNumberOfDaysToKeep <= 0)
            {
                return;
            }

            Logger.WriteLine("Clearing run history older than {0} days", Settings.RunHistoryNumberOfDaysToKeep);
            DateTime clearBeforeDate = DateTime.UtcNow.AddDays(-Settings.RunHistoryNumberOfDaysToKeep);

            if (Settings.RunHistorySavePath != null)
            {
                string file = Path.Combine(Settings.RunHistorySavePath, string.Format("history-{0}.xml", DateTime.Now.ToString("yyyy-MM-ddThh.mm.ss")));
                SyncServer.ClearRunHistory(clearBeforeDate, file);
            }
            else
            {
                SyncServer.ClearRunHistory(clearBeforeDate);
            }
        }

        public static void LoadAndStartMAs()
        {
            maExecutors = new List<MAExecutor>();
            Lithnet.Logging.Logger.OutputToConsole = true;
            EnumerateMAs();
        }

        public static void Stop()
        {
            foreach (MAExecutor x in maExecutors)
            {
                x.Stop();
            }
        }

        internal static void EnumerateMAs()
        {
            foreach (ManagementAgent ma in ManagementAgent.GetManagementAgents())
            {
                IList<object> configItems = GetMAConfigParameters(ma).ToList();

                MAConfigParameters config = MAConfigDiscovery.GetConfig(ma, configItems);

                if (config == null)
                {
                    continue;
                }

                if (!config.Disabled)
                {
                    IList<IMAExecutionTrigger> triggers = MAExecutionTriggerDiscovery.GetExecutionTriggers(ma, config, configItems);
                    MAExecutor x = new MAExecutor(ma, config);
                    x.AttachTrigger(triggers.ToArray());
                    Program.maExecutors.Add(x);
                }
                else
                {
                    Logger.WriteLine("{0}: Skipping management agent because it has been disabled in config", ma.Name);
                }
            }

            foreach (MAExecutor x in maExecutors)
            {
                x.Start();
            }
        }

        private static IEnumerable<object> GetMAConfigParameters(ManagementAgent ma)
        {
            string expectedFileName = Path.Combine(Global.ScriptDirectory, $"Config-{Global.CleanMAName(ma.Name)}.ps1");
            if (!File.Exists(expectedFileName))
            {
                yield break;
            }

            Logger.WriteLine("{0}: Getting configuration from {1}", ma.Name, expectedFileName);

            PowerShell powershell = PowerShell.Create();
            powershell.AddScript(System.IO.File.ReadAllText(expectedFileName));
            powershell.Invoke();

            if (powershell.Runspace.SessionStateProxy.InvokeCommand.GetCommand("Get-MAConfiguration", CommandTypes.All) == null)
            {
                throw new ArgumentException($"The file '{expectedFileName}' must contain a function called Get-MAConfiguration");
            }

            powershell.AddCommand("Get-MAConfiguration");
            Collection<PSObject> results = powershell.Invoke();

            foreach (PSObject o in results)
            {
                Hashtable ht = o.BaseObject as Hashtable;

                if (ht != null)
                {
                    yield return new MAConfigParameters(ht);
                }
                else
                {
                    yield return o.BaseObject;
                }
            }
        }
    }
}