﻿using System.Collections.Generic;
using Lithnet.Miiserver.AutoSync;
using Lithnet.Common.Presentation;

namespace Lithnet.Miiserver.AutoSync.UI.ViewModels
{
    internal class ConfigFileViewModel : ViewModelBase<ConfigFile>
    {
        public ConfigFileViewModel(ConfigFile model)
            :base (model)
        {
            if (model.ManagementAgents == null)
            {
                model.ManagementAgents = new List<MAConfigParameters>();
            }

            this.ManagementAgents = new ManagementAgentsViewModel(model.ManagementAgents);
            this.Settings = new SettingsViewModel(model.Settings);
        }

        public ManagementAgentsViewModel ManagementAgents { get; private set; }

        public SettingsViewModel Settings { get; private set; }

        public override IEnumerable<ViewModelBase> ChildNodes
        {
            get
            {
                yield return this.ManagementAgents;
                yield return this.Settings;
            }
        }
    }
}