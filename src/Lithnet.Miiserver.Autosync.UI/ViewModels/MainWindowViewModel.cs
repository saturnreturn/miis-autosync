﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.ServiceModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Lithnet.Common.ObjectModel;
using Lithnet.Common.Presentation;
using MahApps.Metro.Controls;
using Microsoft.Win32;

namespace Lithnet.Miiserver.AutoSync.UI.ViewModels
{

    internal class MainWindowViewModel : ViewModelBase
    {
        private List<Type> ignoreViewModelChanges;

        private bool confirmedCloseOnDirtyViewModel;

        public ConfigFileViewModel ConfigFile { get; set; }

        public string DisplayName => "Lithnet AutoSync for Microsoft Identity Manager" + (this.ViewModelIsDirty ? "*" : string.Empty);

        private bool ViewModelIsDirty { get; set; }

        public Cursor Cursor { get; set; }
        
        public MainWindowViewModel()
        {
            UINotifyPropertyChanges.BeginIgnoreAllChanges();
            this.PopulateIgnoreViewModelChanges();
            this.AddDependentPropertyNotification("ViewModelIsDirty", "DisplayName");

            this.IgnorePropertyHasChanged.Add("DisplayName");
            this.IgnorePropertyHasChanged.Add("ChildNodes");
            this.IgnorePropertyHasChanged.Add("ViewModelIsDirty");

            this.Commands.AddItem("Reload", x => this.Reload());
            this.Commands.AddItem("Save", x => this.Save(), x => this.CanSave());
            this.Commands.AddItem("Export", x => this.Export(), x => this.CanExport());
            this.Commands.AddItem("Close", x => this.Close());
            this.Commands.AddItem("Import", x => this.Import(), x => this.CanImport());

            this.Cursor = Cursors.Arrow;

            ViewModelBase.ViewModelChanged += this.ViewModelBase_ViewModelChanged;
            Application.Current.MainWindow.Closing += this.MainWindow_Closing;
            UINotifyPropertyChanges.EndIgnoreAllChanges();
        }

        private void PopulateIgnoreViewModelChanges()
        {
            this.ignoreViewModelChanges = new List<Type>();
        }

        private void Reload(bool confirmRestart = true)
        {
            this.UpdateFocusedBindings();

            if (this.ViewModelIsDirty)
            {
                if (MessageBox.Show("There are unsaved changes. Are you sure you want to reload the config?", "Unsaved Changes", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No)
                {
                    return;
                }
            }

            if (confirmRestart)
            {
                if (MessageBox.Show("This will force the AutoSync service to stop and restart with the latest configuration. Are you sure you want to proceed?", "Confirm", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.Cancel)
                {
                    return;
                }
            }

            try
            {
                this.Cursor = Cursors.Wait;
                Task.Run(() =>
                {
                    ConfigClient c = new ConfigClient();
                    c.Reload();
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred while reloading the service config. Check the service log file for details\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                this.Cursor = Cursors.Arrow;
            }

            this.ResetConfigViewModel();
        }

        internal void ResetConfigViewModel()
        {
            try
            {
                this.Cursor = Cursors.Wait;
                UINotifyPropertyChanges.BeginIgnoreAllChanges();

                ConfigClient c = new ConfigClient();
                ConfigFile file;

                try
                {
                    c.Open();
                    file = c.GetConfig();
                }
                catch (EndpointNotFoundException ex)
                {
                    Trace.WriteLine(ex);
                    MessageBox.Show(
                        $"Could not contact the AutoSync service. Ensure the Lithnet MIIS AutoSync service is running",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(ex);
                    MessageBox.Show(
                        $"An error occurred communicating with the AutoSync service\n\n{ex.Message}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                this.ConfigFile = new ConfigFileViewModel(file);
                this.ConfigFile.ManagementAgents.IsExpanded = true;

                this.ViewModelIsDirty = false;
            }
            finally
            {
                UINotifyPropertyChanges.EndIgnoreAllChanges();
                this.Cursor = Cursors.Arrow;
            }
        }

        private void Import()
        {
            this.UpdateFocusedBindings();

            if (this.ViewModelIsDirty)
            {
                if (MessageBox.Show("There are unsaved changes. Do you want to continue?", "Unsaved Changes", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No)
                {
                    return;
                }
            }

            OpenFileDialog dialog = new OpenFileDialog();
            dialog.DefaultExt = ".xml";
            dialog.Filter = "Configuration Files (.xml)|*.xml|All Files|*.*";
            dialog.CheckFileExists = true;

            bool? result = dialog.ShowDialog();

            if (result != true)
            {
                return;
            }

            if (!System.IO.File.Exists(dialog.FileName))
            {
                return;
            }

            try
            {
                AutoSync.ConfigFile f;
                try
                {
                    f = AutoSync.ConfigFile.Load(dialog.FileName);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(ex);
                    MessageBox.Show($"Could not open the file\n\n{ex.Message}", "File Open", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                try
                {
                    this.Cursor = Cursors.Wait;
                    ConfigClient c = new ConfigClient();
                    c.PutConfig(f);
                    this.AskToRestartService();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(ex);
                    MessageBox.Show($"Could not import the file\n\n{ex.Message}", "File import operation", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                finally
                {
                    this.Cursor = Cursors.Arrow;
                }
            }
            finally
            {
                this.ResetConfigViewModel();
            }

            this.ViewModelIsDirty = false;
        }

        private void Save()
        {
            this.UpdateFocusedBindings();

            try
            {
                this.Cursor = Cursors.Wait;
                ConfigClient c = new ConfigClient();
                c.PutConfig(this.ConfigFile.Model);
                this.ViewModelIsDirty = false;

                foreach (MAConfigParametersViewModel p in this.ConfigFile.ManagementAgents)
                {
                    p.IsNew = false;
                }

                this.AskToRestartService();
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex);
                MessageBox.Show($"Could not save the configuration\n\n{ex.Message}", "Save operation", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                this.Cursor = Cursors.Arrow;
            }
        }

        private bool CanSave()
        {
            return this.ConfigFile != null;
        }

        private bool CanImport()
        {
            return this.ConfigFile != null;
        }

        private bool CanExport()
        {
            return this.ConfigFile != null;
        }

        private void AskToRestartService()
        {
            if (MessageBox.Show("Do you want to restart the service now to make the new config take effect?", "Error", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.No)
            {
                return;
            }

            this.Reload(false);
        }

        private void Export()
        {
            this.UpdateFocusedBindings();

            if (this.HasErrors)
            {
                if (MessageBox.Show("There are one or more errors present in the configuration. Are you sure you want to save?", "Error", MessageBoxButton.YesNo, MessageBoxImage.Error) == MessageBoxResult.No)
                {
                    return;
                }
            }

            if (MessageBox.Show("Any passwords in the configuration will be exported in plain-text. Are you sure you want to continue?", "Warning", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.No)
            {
                return;
            }

            try
            {
                SaveFileDialog dialog = new SaveFileDialog();
                dialog.DefaultExt = ".xml";
                dialog.Filter = "Configuration file backups (*.xml)|*.xml|All Files|*.*";

                bool? result = dialog.ShowDialog();

                if (result == true)
                {
                    this.Cursor = Cursors.Wait;
                    ProtectedString.EncryptOnWrite = false;
                    AutoSync.ConfigFile.Save(this.ConfigFile.Model, dialog.FileName);
                    this.ViewModelIsDirty = false;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex);
                MessageBox.Show($"Could not save the file\n\n{ex.Message}", "Save File", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                this.Cursor = Cursors.Arrow;
            }
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            this.UpdateFocusedBindings();

            if (this.ViewModelIsDirty && !this.confirmedCloseOnDirtyViewModel)
            {
                if (MessageBox.Show("There are unsaved changes. Are you sure you want to exit?", "Unsaved Changes", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No)
                {
                    e.Cancel = true;
                }
            }
        }

        private void ViewModelBase_ViewModelChanged(object sender, PropertyChangedEventArgs e)
        {
            if (sender == this)
            {
                return;
            }

            if (this.ViewModelIsDirty)
            {
                return;
            }

            if (this.IgnorePropertyHasChanged.Contains(e.PropertyName))
            {
                return;
            }

            if (this.ignoreViewModelChanges.Contains(sender.GetType()))
            {
                return;
            }

            this.ViewModelIsDirty = true;
            this.RaisePropertyChanged("DisplayName");
        }

        private void Close()
        {
            this.UpdateFocusedBindings();

            if (this.ViewModelIsDirty)
            {
                if (MessageBox.Show("There are unsaved changes. Are you sure you want to exit?", "Unsaved Changes", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No)
                {
                    return;
                }
            }

            this.confirmedCloseOnDirtyViewModel = true;
            Application.Current.Shutdown();
        }

        private void UpdateFocusedBindings()
        {
            object focusedItem = Keyboard.FocusedElement;

            if (focusedItem == null)
            {
                return;
            }

            BindingExpression expression = (focusedItem as TextBox)?.GetBindingExpression(TextBox.TextProperty);
            expression?.UpdateSource();

            expression = (focusedItem as ComboBox)?.GetBindingExpression(ComboBox.TextProperty);
            expression?.UpdateSource();

            expression = (focusedItem as PasswordBox)?.GetBindingExpression(PasswordBoxBindingHelper.PasswordProperty);
            expression?.UpdateSource();

            expression = (focusedItem as TimeSpanControl)?.GetBindingExpression(TimeSpanControl.ValueProperty);
            expression?.UpdateSource();

            expression = (focusedItem as DateTimePicker)?.GetBindingExpression(DateTimePicker.SelectedDateProperty);
            expression?.UpdateSource();
        }
    }
}