﻿using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Lithnet.Common.Presentation;
using Lithnet.Miiserver.AutoSync;
using Microsoft.Win32;
using PropertyChanged;

namespace Lithnet.Miiserver.AutoSync.UI.ViewModels
{
    public class PowerShellExecutionTriggerViewModel : MAExecutionTriggerViewModel
    {
        private PowerShellExecutionTrigger typedModel;

        public PowerShellExecutionTriggerViewModel(PowerShellExecutionTrigger model)
            : base(model)
        {
            this.typedModel = model;
            this.Commands.Add("Browse", new DelegateCommand(t => this.Browse()));
            this.Commands.Add("New", new DelegateCommand(t => this.New()));
            this.Commands.Add("Edit", new DelegateCommand(t => this.Edit(), u => this.CanEdit()));
            this.AddIsDirtyProperty(nameof(this.ScriptPath));
            this.AddIsDirtyProperty(nameof(this.Interval));
            this.AddIsDirtyProperty(nameof(this.ExceptionBehaviour));
        }

        [AlsoNotifyFor("Description")]
        public string ScriptPath
        {
            get => this.typedModel.ScriptPath;
            set => this.typedModel.ScriptPath = value;
        }

        public TimeSpan Interval
        {
            get => this.typedModel.Interval;
            set => this.typedModel.Interval = value;
        }

        public ExecutionErrorBehaviour ExceptionBehaviour
        {
            get => this.typedModel.ExceptionBehaviour;
            set => this.typedModel.ExceptionBehaviour = value;
        }

        public string Type => this.Model.Type;

        public string Description => this.Model.Description;

        private void New()
        {
            SaveFileDialog dialog = new SaveFileDialog
            {
                AddExtension = true,
                DefaultExt = "ps1",
                OverwritePrompt = true,
                Filter = "PowerShell script|*.ps1"
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                File.WriteAllText(dialog.FileName, Properties.Resources.PowerShellTriggerScript);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save the file\n{ex.Message}", "Unable to save");
                return;
            }

            this.ScriptPath = dialog.FileName;

            this.Edit();
        }

        private bool CanEdit()
        {
            return File.Exists(this.ScriptPath);
        }

        private void Edit()
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo(this.ScriptPath) { Verb = "Edit" };
                Process newProcess = new Process { StartInfo = startInfo };
                newProcess.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open the file\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Browse()
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();

            if (File.Exists(this.ScriptPath))
            {
                try
                {
                    openFileDialog.InitialDirectory = Path.GetDirectoryName(this.ScriptPath);
                    openFileDialog.FileName = Path.GetFileName(this.ScriptPath);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("Error parsing file path\n" + ex);
                }
            }
            else
            {
                openFileDialog.FileName = "*.ps1";
            }

            openFileDialog.AddExtension = true;
            openFileDialog.CheckFileExists = true;
            openFileDialog.DefaultExt = "ps1";
            openFileDialog.Filter = "PowerShell script|*.ps1";

            if (openFileDialog.ShowDialog() == true)
            {
                this.ScriptPath = openFileDialog.FileName;
            }
        }

        protected override void ValidatePropertyChange(string propertyName)
        {
            if (propertyName == nameof(this.ScriptPath))
            {
                if (System.IO.File.Exists(this.ScriptPath))
                {
                    this.RemoveError(nameof(this.ScriptPath));
                }
                else
                {
                    this.AddError(nameof(this.ScriptPath), "The specified file was not found");
                }
            }

            base.ValidatePropertyChange(propertyName);
        }
    }
}
