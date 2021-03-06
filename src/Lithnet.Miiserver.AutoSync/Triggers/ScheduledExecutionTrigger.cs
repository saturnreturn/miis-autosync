﻿using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.Serialization;
using System.Timers;
using Lithnet.Miiserver.Client;

namespace Lithnet.Miiserver.AutoSync
{
    [DataContract(Name = "scheduled-trigger")]
    [Description(TypeDescription)]
    public class ScheduledExecutionTrigger : MAExecutionTrigger
    {
        private const string TypeDescription = "Scheduled interval";

        private Timer checkTimer;

        [DataMember(Name = "start-date")]
        public DateTime StartDateTime { get; set; }

        [DataMember(Name = "interval")]
        public TimeSpan Interval { get; set; }

        [DataMember(Name = "run-profile-name")]
        public string RunProfileName { get; set; }

        [DataMember(Name = "exclusive")]
        public bool Exclusive { get; set; }

        private double RemainingMilliseconds { get; set; }

        private void SetRemainingMilliseconds()
        {
            if (this.Interval.TotalSeconds < 1)
            {
                throw new ArgumentException("The interval cannot be zero");
            }

            if (this.StartDateTime == new DateTime(0))
            {
                this.StartDateTime = DateTime.Now;
            }

            DateTime triggerTime = this.StartDateTime;
            DateTime now = DateTime.Now;

            while (triggerTime < now)
            {
                triggerTime = triggerTime.Add(this.Interval);
            }

            this.Trace("Scheduling event for " + triggerTime);
            this.RemainingMilliseconds = (triggerTime - now).TotalMilliseconds;
        }

        private void CheckTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            this.Fire(this.RunProfileName, this.Exclusive);
            this.ResetTimer();
        }

        public override void Start(string managementAgentName)
        {
            this.ManagementAgentName = managementAgentName;

            if (this.RunProfileName == null)
            {
                this.LogError("Ignoring scheduled trigger with no run profile name");
                return;
            }

            this.ResetTimer();
        }

        private void ResetTimer()
        {
            this.SetRemainingMilliseconds();
            this.checkTimer = new Timer
            {
                Interval = this.RemainingMilliseconds,
                AutoReset = false
            };

            this.checkTimer.Elapsed += this.CheckTimer_Elapsed;
            this.checkTimer.Start();
        }

        public override void Stop()
        {
            if (this.checkTimer == null)
            {
                return;
            }

            if (this.checkTimer.Enabled)
            {
                this.checkTimer.Stop();
            }
        }

        public override string DisplayName => $"{this.Type}: {this.Description}";

        public override string Type => TypeDescription;

        public override string Description => $"{this.RunProfileName} every {this.Interval} starting from {this.StartDateTime}";

        public override string ToString()
        {
            return $"{this.DisplayName}";
        }

        public static bool CanCreateForMA(ManagementAgent ma)
        {
            return true;
        }

        public ScheduledExecutionTrigger(ManagementAgent ma)
        {
            this.RunProfileName = ma.RunProfiles?.Select(u => u.Key).FirstOrDefault();
            this.Interval = new TimeSpan(24, 0, 0);
            this.StartDateTime = DateTime.Now;
            this.StartDateTime = this.StartDateTime.AddSeconds(-this.StartDateTime.Second);
        }
    }
}
