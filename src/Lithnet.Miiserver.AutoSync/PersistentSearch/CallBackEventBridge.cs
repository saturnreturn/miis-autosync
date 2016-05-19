﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Lithnet.Miiserver.AutoSync
{
    public sealed class CallbackEventBridge
    {
        public event AsyncCallback CallbackComplete = delegate { };

        private CallbackEventBridge()
        {
        }

        private void CallbackInternal(IAsyncResult result)
        {
            CallbackComplete(result);
        }

        public AsyncCallback Callback
        {
            get
            {
                return new AsyncCallback(CallbackInternal);
            }
        }

        public static CallbackEventBridge Create()
        {
            return new CallbackEventBridge();
        }
    }
}