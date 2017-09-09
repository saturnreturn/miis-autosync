﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using System.Text;

namespace Lithnet.Miiserver.AutoSync
{
    internal static class RegistrySettings
    {
        private static RegistryKey parametersKey;

        private static RegistryKey serviceKey;

        private static RegistryKey userSettingsKey;
        
        private static HashSet<string> retryCodes;

        public static RegistryKey UserSettingsKey
        {
            get
            {
                if (RegistrySettings.userSettingsKey == null)
                {
                    RegistrySettings.userSettingsKey = Registry.CurrentUser.CreateSubKey("Software\\Lithnet\\AutoSync");
                }

                return RegistrySettings.userSettingsKey;
            }
        }
        
        public static RegistryKey ParametersKey
        {
            get
            {
                if (RegistrySettings.parametersKey == null)
                {
                    RegistrySettings.parametersKey = Registry.LocalMachine.OpenSubKey("System\\CurrentControlSet\\Services\\autosync\\Parameters", true);
                }

                return RegistrySettings.parametersKey;
            }
        }

        public static RegistryKey ServiceKey
        {
            get
            {
                if (RegistrySettings.serviceKey == null)
                {
                    RegistrySettings.serviceKey = Registry.LocalMachine.OpenSubKey("System\\CurrentControlSet\\Services\\autosync", false);
                }

                return RegistrySettings.serviceKey;
            }
        }

        public static string ServiceAccount => (string)RegistrySettings.ServiceKey.GetValue("ObjectName", null);

        public static bool AutoStartEnabled
        {
            get
            {
                int? value = RegistrySettings.ParametersKey.GetValue(nameof(AutoStartEnabled), 1) as int?;
                return value.HasValue && value.Value != 0;
            }
            set
            {
                RegistrySettings.ParametersKey.SetValue(nameof(AutoStartEnabled), value ? 1 : 0, RegistryValueKind.DWord);
            }
        }

        public static bool GetSyncLockForFimMAExport
        {
            get
            {
                int? value = RegistrySettings.ParametersKey.GetValue(nameof(GetSyncLockForFimMAExport), 0) as int?;
                return value.HasValue && value.Value != 0;
            }
        }

        public static string LogPath
        {
            get
            {
                string logPath = RegistrySettings.ParametersKey.GetValue(nameof(LogPath)) as string;

                if (logPath != null)
                {
                    return Path.Combine(logPath, "autosync.log");
                }

                string codeBase = Assembly.GetExecutingAssembly().CodeBase;
                UriBuilder uri = new UriBuilder(codeBase);
                string path = Uri.UnescapeDataString(uri.Path);
                string dirName = Path.GetDirectoryName(path);

                return Path.Combine(dirName ?? Global.AssemblyDirectory, "Logs\\autosync.log");
            }
        }

        public static string ServiceAdminsGroup
        {
            get
            {
                string value = RegistrySettings.ParametersKey.GetValue(nameof(ServiceAdminsGroup)) as string;

                if (value != null)
                {
                    return value;
                }

                return "FimSyncAdmins";
            }
        }

        public static bool NetTcpServerEnabled
        {
            get
            {
                int? value = RegistrySettings.ParametersKey.GetValue(nameof(NetTcpServerEnabled), 0) as int?;
                return value.HasValue && value.Value != 0;
            }
        }

        public static string NetTcpBindAddress
        {
            get
            {
                return (string)RegistrySettings.ParametersKey.GetValue(nameof(NetTcpBindAddress), "0.0.0.0");
            }
        }

        public static string NetTcpBindPort
        {
            get
            {
                return (string)RegistrySettings.ParametersKey.GetValue(nameof(NetTcpBindPort), "54338");
            }
        }


        public static string AutoSyncServerHost
        {
            get
            {
                return (string)RegistrySettings.UserSettingsKey.GetValue(nameof(AutoSyncServerHost), "localhost");
            }
        }

        public static string AutoSyncServerPort
        {
            get
            {
                return (string)RegistrySettings.UserSettingsKey.GetValue(nameof(AutoSyncServerPort), "54338");
            }
        }

        public static string AutoSyncServerIdentity
        {
            get
            {
                return (string)RegistrySettings.UserSettingsKey.GetValue(nameof(AutoSyncServerIdentity), "autosync/{0}");
            }
        }


        public static string ConfigurationFile
        {
            get
            {
                string path = RegistrySettings.ParametersKey.GetValue("ConfigFile") as string;

                if (path != null)
                {
                    return path;
                }

                return Path.Combine(Global.AssemblyDirectory, "config.xml");
            }
        }

        public static int RetryCount
        {
            get
            {
                string s = RegistrySettings.ParametersKey.GetValue(nameof(RetryCount)) as string;

                int value;

                return int.TryParse(s, out value) ? value : 5;
            }
        }

        public static HashSet<string> RetryCodes
        {
            get
            {
                if (retryCodes == null)
                {
                    retryCodes = new HashSet<string>();

                    string[] values = RegistrySettings.ParametersKey.GetValue(nameof(RetryCodes)) as string[];

                    if (values != null && values.Length > 0)
                    {
                        foreach (string s in values)
                        {
                            retryCodes.Add(s);
                        }
                    }
                    else
                    {
                        retryCodes.Add("stopped-deadlocked");
                    }
                }

                return retryCodes;
            }
        }

        /// <summary>
        /// The amount of time to sleep after a deadlock event before retrying
        /// </summary>
        public static TimeSpan RetrySleepInterval
        {
            get
            {
                string s = RegistrySettings.ParametersKey.GetValue(nameof(RetrySleepInterval)) as string;

                int seconds;

                if (int.TryParse(s, out seconds))
                {
                    return new TimeSpan(0, 0, seconds >= 1 ? seconds : 1);
                }
                else
                {
                    return new TimeSpan(0, 0, 5);
                }
            }
        }

        public static TimeSpan ExecutionStaggerInterval
        {
            get
            {
                string s = RegistrySettings.ParametersKey.GetValue(nameof(ExecutionStaggerInterval)) as string;

                int seconds;

                if (int.TryParse(s, out seconds))
                {
                    return new TimeSpan(0, 0, seconds >= 1 ? seconds : 1);
                }
                else
                {
                    return new TimeSpan(0, 0, 2);
                }
            }
        }

        /// <summary>
        /// The amount of time after the run profile completes before analysis of the run profile results starts
        /// </summary>
        public static TimeSpan PostRunInterval
        {
            get
            {
                string s = RegistrySettings.ParametersKey.GetValue(nameof(PostRunInterval)) as string;

                int seconds;

                if (int.TryParse(s, out seconds))
                {
                    return new TimeSpan(0, 0, seconds >= 1 ? seconds : 1);
                }
                else
                {
                    return new TimeSpan(0, 0, 2);
                }
            }
        }

        public static TimeSpan UnmanagedChangesCheckInterval
        {
            get
            {
                string s = RegistrySettings.ParametersKey.GetValue(nameof(UnmanagedChangesCheckInterval)) as string;

                int seconds;

                if (int.TryParse(s, out seconds))
                {
                    return new TimeSpan(0, 0, seconds > 0 ? seconds : 3600);
                }
                else
                {
                    return new TimeSpan(0, 60, 0);
                }
            }
        }

        public static TimeSpan RunHistoryTimerInterval
        {
            get
            {
                string s = RegistrySettings.ParametersKey.GetValue(nameof(RunHistoryTimerInterval)) as string;

                int seconds;

                if (int.TryParse(s, out seconds))
                {
                    if (seconds > 0)
                    {
                        return new TimeSpan(0, 0, seconds);
                    }
                    else
                    {
                        return TimeSpan.FromHours(8);
                    }
                }
                else
                {
                    return TimeSpan.FromHours(8);
                }
            }
        }
    }
}

