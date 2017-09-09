﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using Lithnet.Miiserver.AutoSync;
using Lithnet.Common.Presentation;

namespace Lithnet.Miiserver.AutoSync.UI.ViewModels
{
    internal class ExecutionMonitorsViewModel : ListViewModel<ExecutionMonitorViewModel, string>
    {
        public string DisplayName => "Execution Monitor";

        public bool AutoStartEnabled
        {
            get
            {
                ConfigClient c = ConfigClient.GetDefaultClient();
                return c.InvokeThenClose(x => x.GetAutoStartState());
            }
            set
            {
                ConfigClient c = ConfigClient.GetDefaultClient();
                c.InvokeThenClose(x => x.SetAutoStartState(value));
            }
        }

        public ExecutionMonitorsViewModel(IList<string> items)
            : base(items, ExecutionMonitorsViewModel.ViewModelResolver)
        {
            this.Commands.AddItem("StartEngine", x => this.StartEngine(), x => this.CanStartEngine());
            this.Commands.AddItem("StopEngine", x => this.StopEngine(false), x => this.CanStopEngine());
            this.Commands.AddItem("StopEngineAndCancelRuns", x => this.StopEngine(true), x => this.CanStopEngine());

            this.DisplayIcon = App.GetImageResource("Monitor.ico");

            ExecutionMonitorViewModel vm = this.ViewModels.FirstOrDefault();
            if (vm != null)
            {
                vm.IsSelected = true;
            }
        }

        private static ExecutionMonitorViewModel ViewModelResolver(string model)
        {
            return new ExecutionMonitorViewModel(model);
        }
        
        private void StartEngine()
        {
            try
            {
                ConfigClient c = ConfigClient.GetDefaultClient();
                c.InvokeThenClose(x => x.StartAll());
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex);
                MessageBox.Show($"Error starting the management agents\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool CanStartEngine()
        {
            return this.ViewModels.Any(t => t.ControlState == ControlState.Stopped);
        }

        private void StopEngine(bool cancelRuns)
        {
            try
            {
                ConfigClient c = ConfigClient.GetDefaultClient();
                c.InvokeThenClose(x => x.StopAll(cancelRuns));
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex);
                MessageBox.Show($"Error stopping the management agents\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool CanStopEngine()
        {
            return this.ViewModels.Any(t => t.ControlState == ControlState.Running);
        }
    }
}
