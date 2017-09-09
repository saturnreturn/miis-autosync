﻿using System;
using System.ServiceModel.Channels;
using System.ServiceModel;
using System.ServiceModel.Description;

namespace Lithnet.Miiserver.AutoSync
{
    public class EventServiceConfiguration
    {
        public const string NamedPipeUri = "net.pipe://localhost/lithnet/autosync/events";

        public const string TcpUri = "net.tcp://{0}:{1}/lithnet/autosync/events";

        public static ServiceMetadataBehavior ServiceMetadataDisabledBehavior
        {
            get
            {
                return new ServiceMetadataBehavior
                {
                    HttpGetEnabled = false,
                    HttpsGetEnabled = false
                };
            }
        }

        public static ServiceDebugBehavior ServiceDebugBehavior
        {
            get
            {
                return new ServiceDebugBehavior
                {
                    IncludeExceptionDetailInFaults = true,
                    HttpHelpPageEnabled = false,
                    HttpsHelpPageEnabled = false
                };
            }
        }

        public static Binding NetNamedPipeBinding
        {
            get
            {
                int timeout = System.Diagnostics.Debugger.IsAttached ? 60 : 60;

                NetNamedPipeBinding binding = new NetNamedPipeBinding();
                binding.MaxReceivedMessageSize = int.MaxValue;
                binding.ReaderQuotas.MaxStringContentLength = int.MaxValue;
                binding.ReaderQuotas.MaxBytesPerRead = int.MaxValue;
                binding.CloseTimeout = TimeSpan.FromSeconds(timeout);
                binding.OpenTimeout = TimeSpan.FromSeconds(timeout);
                binding.ReceiveTimeout = TimeSpan.MaxValue;
                binding.SendTimeout = TimeSpan.MaxValue;
                binding.TransactionFlow = false;
                binding.Security.Mode = NetNamedPipeSecurityMode.Transport;
                binding.Security.Transport.ProtectionLevel = System.Net.Security.ProtectionLevel.None;
                return binding;
            }
        }

        public static Binding NetTcpBinding
        {
            get
            {
                int timeout = System.Diagnostics.Debugger.IsAttached ? 900 : 100;

                NetTcpBinding binding = new NetTcpBinding();
                binding.MaxReceivedMessageSize = int.MaxValue;
                binding.ReaderQuotas.MaxStringContentLength = int.MaxValue;
                binding.ReaderQuotas.MaxBytesPerRead = int.MaxValue;
                binding.CloseTimeout = TimeSpan.FromSeconds(timeout);
                binding.OpenTimeout = TimeSpan.FromSeconds(timeout);
                binding.ReceiveTimeout = TimeSpan.FromSeconds(timeout);
                binding.SendTimeout = TimeSpan.FromSeconds(timeout);
                binding.TransactionFlow = false;
                binding.Security.Mode = SecurityMode.Message;
                binding.Security.Transport.ProtectionLevel = System.Net.Security.ProtectionLevel.EncryptAndSign;

                return binding;
            }
        }

        public static EndpointAddress NetNamedPipeEndpointAddress
        {
            get
            {
                return new EndpointAddress(EventServiceConfiguration.NamedPipeUri);
            }
        }

        public static EndpointAddress CreateClientTcpEndPointAddress()
        {
            return EventServiceConfiguration.CreateTcpEndPointAddress(RegistrySettings.AutoSyncServerHost, RegistrySettings.AutoSyncServerPort);
        }

        public static string CreateServerBindingUri()
        {
            return EventServiceConfiguration.CreateTcpUri(RegistrySettings.NetTcpBindAddress, RegistrySettings.NetTcpBindPort);
        }


        public static EndpointAddress CreateTcpEndPointAddress(string hostname, string port)
        {
            EndpointIdentity i;

            string expectedServerIdentity = string.Format(RegistrySettings.AutoSyncServerIdentity, hostname);

            if (expectedServerIdentity.Contains("@"))
            {
                i = EndpointIdentity.CreateUpnIdentity(expectedServerIdentity);
            }
            else
            {
                i = EndpointIdentity.CreateSpnIdentity(expectedServerIdentity);
            }

            return new EndpointAddress(new Uri(EventServiceConfiguration.CreateTcpUri(hostname, port)), i);
        }

        public static string CreateTcpUri(string hostname, string port)
        {
            return string.Format(EventServiceConfiguration.TcpUri, hostname, port);
        }
    }
}