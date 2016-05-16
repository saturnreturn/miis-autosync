﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Management.Automation.Host;
using System.Threading.Tasks;
using System.Threading;
using Lithnet.Logging;

namespace Lithnet.Miiserver.AutoSync
{
    public class PowerShellExecutionTrigger : IMAExecutionTrigger
    {
        private bool run = true;

        public string ScriptPath { get; set; }

        public string Name
        {
            get
            {
                return System.IO.Path.GetFileName(this.ScriptPath);
            }
        }

        public event ExecutionTriggerEventHandler TriggerExecution;

        public void Start()
        {
            Task t = new Task(() =>
            {
                try
                {
                    PowerShell powershell = PowerShell.Create();
                    powershell.AddScript(System.IO.File.ReadAllText(this.ScriptPath));
                    powershell.Invoke();

                    if (powershell.Runspace.SessionStateProxy.InvokeCommand.GetCommand("Get-RunProfileToExecute", CommandTypes.All) == null)
                    {
                        Logger.WriteLine("The file '{0}' did not contain a function called Get-RunProfileToExecute and will be ignored", this.ScriptPath);
                        return;
                    }

                    while (this.run)
                    {
                        powershell.AddCommand("Get-RunProfileToExecute");

                        foreach (PSObject result in powershell.Invoke())
                        {
                            string runProfileName = result.BaseObject as string;

                            if (runProfileName != null)
                            {
                                this.Fire(runProfileName);
                                continue;
                            }

                            ExecutionParameters p = result.BaseObject as ExecutionParameters;

                            if (p != null)
                            {
                                this.Fire(p);
                                continue;
                            }
                        }

                        Thread.Sleep(5000);
                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("The PowerShell execution trigger encountered an error and has been terminated");
                    Logger.WriteException(ex);
                }
            });

            t.Start();
        }

        public void Stop()
        {
            this.run = false;
        }

        public void Fire(string runProfileName)
        {
            ExecutionTriggerEventHandler registeredHandlers = this.TriggerExecution;

            if (registeredHandlers != null)
            {
                registeredHandlers(this, new ExecutionTriggerEventArgs(runProfileName));
            }
        }

        public void Fire(ExecutionParameters p)
        {
            ExecutionTriggerEventHandler registeredHandlers = this.TriggerExecution;

            if (registeredHandlers != null)
            {
                registeredHandlers(this, new ExecutionTriggerEventArgs(p));
            }
        }
    }
}
