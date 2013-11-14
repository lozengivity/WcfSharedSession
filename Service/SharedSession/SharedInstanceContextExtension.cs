﻿using System;
using System.Collections.Generic;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Dispatcher;

namespace Service.SharedSession
{
    public class SharedInstanceContextExtension : IInstanceContextProvider, IDispatchMessageInspector
    {
        // contextMap maps from an id in a message to a AddressableInstanceContextInfo.  The info
        // provides a rendezvous point for code interested in the InstanceContext corresponding to
        // that id.  We cannot just store the InstanceContext here because we need to coordinate
        // threads before the InstanceContext exists.
        Dictionary<string, AddressableInstanceContextInfo> contextMap;

        public SharedInstanceContextExtension()
        {
            this.contextMap = new Dictionary<string, AddressableInstanceContextInfo>();
        }

        // Locking this object guards access to contextMap.
        object ThisLock
        {
            get { return this.contextMap; }
        }

        //
        // WCF calls this before throttling to determine if there is already an InstanceContext for
        // this message and channel, or if WCF needs to create a new one.  Returning null instructs
        // WCF to create a new InstanceContext for this message.  WCF's throttle keeps
        // track of how many InstanceContexts WCF creates, so any InstanceContexts you create here
        // and return do not count against that throttle.  Because of this, you should generally
        // avoid creating new InstanceContexts in this method.
        //
        // This is called for every message bound for user code.
        //
        InstanceContext IInstanceContextProvider.GetExistingInstanceContext(Message message, IContextChannel channel)
        {
            AddressableInstanceContextInfo info;

            // If the channel has a session, we bind the session to a particular InstanceContext
            // based on the first message, and then route all subsequent messages on that session to
            // the same InstanceContext.
            bool hasSession = (channel.SessionId != null);
            if (hasSession)
            {
                info = channel.Extensions.Find<AddressableInstanceContextInfo>();
                if (info != null)
                {
                    // We may be processing a second message before the first message has finished
                    // initializing the InstanceContext.  Wait here until the first message is
                    // done.  If the first message has already finished initializing, this returns
                    // immediately.
                    info.IncrementBusyCount();
                    return info.WaitForInstanceContext();
                }
            }

            // If this is the first message of a session, or is using a datagram channel, look in
            // the message headers to see if there is a header with an instance id.
            int headerIndex = message.Headers.FindHeader(SharedInstanceHeader.HeaderName, SharedInstanceHeader.HeaderNamespace);

            // If there was a header, extract the instanceId.
            string instanceId = null;
            if (headerIndex != -1)
            {
                instanceId = message.Headers.GetHeader<string>(headerIndex);
            }

            // Remember if we created a new AddressableInstanceContextInfo.
            bool isNew = false;

            // Check our table to see if we recognize the instance id.
            lock (this.ThisLock)
            {
                if ((instanceId == null) || !this.contextMap.TryGetValue(instanceId, out info))
                {
                    // Either there is no instance id, or if there is we did not recognize it.
                    // Create a new entry in the table.
                    info = new AddressableInstanceContextInfo(this, instanceId);
                    isNew = true;
                    this.contextMap.Add(instanceId, info);
                }

                // Mark this entry as busy so we don't unload it.
                info.IncrementBusyCount();
            }

            // If the channel has a session, attach the info to the channel so we will find it on
            // the next message.
            if (hasSession)
            {
                channel.Extensions.Add(info);
            }

            if (isNew)
            {
                // This tells WCF to create a new InstanceContext and call InitializeInstanceContext.
                return null;
            }
            else
            {
                InstanceContext instanceContext = info.WaitForInstanceContext();
                if (hasSession)
                {
                    // Just like InstanceContextMode.PerSession, we store the sessionful channel in the
                    // InstanceContext.  This enables users to inspect the list of channels, causes the
                    // channels to be closed when the InstanceContext is explicitly closed, and
                    // automatically keeps the InstanceContext alive.
                    instanceContext.IncomingChannels.Add(channel);
                }
                return instanceContext;
            }
        }

        //
        // WCF calls this after creating a new InstanceContext because we returned null.  This is
        // our chance to do any initialization on the instanceContext before it is used to process
        // the message.  This is called once per created InstanceContext, not for every message.
        //
        void IInstanceContextProvider.InitializeInstanceContext(InstanceContext instanceContext,
                                                                Message message, IContextChannel channel)
        {
            AddressableInstanceContextInfo info = null;
            bool hasSession = (channel.SessionId != null);

            if (hasSession)
            {
                // Since this is a new InstanceContext, we could not add the channel in
                // GetExistingInstanceContext, so add it here.
                instanceContext.IncomingChannels.Add(channel);

                // If we have a session, we stored the info in the channel, so just look it up
                // there.
                info = channel.Extensions.Find<AddressableInstanceContextInfo>();
            }
            else
            {
                // Otherwise, if we don't have a session, look the info up again in the table.
                int headerIndex = message.Headers.FindHeader(SharedInstanceHeader.HeaderName, SharedInstanceHeader.HeaderNamespace);
                if (headerIndex != -1)
                {
                    string instanceId = message.Headers.GetHeader<string>(headerIndex);
                    if (instanceId != null)
                    {
                        this.contextMap.TryGetValue(instanceId, out info);
                    }
                }
            }

            // Now that we have the InstanceContext, we can link it to the
            // AddressableInstanceContextInfo and vice versa.
            if (info != null)
            {
                instanceContext.Extensions.Add(info);
                info.SetInstanceContext(instanceContext);
            }

            // When the InstanceContext starts closing, remove it from the table.
            //
            // Generally we will already have the lock because Close will happen inside
            // CallIdleCallback.  However, if someone just closes the InstanceContext explicitly
            // before it goes idle, we will not have the lock.  Since modifying Dictionary is not
            // thread-safe, we lock here.
            instanceContext.Closing += delegate(object sender, EventArgs e)
                {
                    lock (this.ThisLock)
                    {
                        this.contextMap.Remove(info.InstanceId);
                    }
                };
        }

        //
        // WCF calls this when all the channels attached to an InstanceContext are closed and all
        // the messages are finished processing.
        //
        // If we return true, the instanceContext will close.  If we return false, WCF will call
        // NotifyIdle so we can call them back when the instanceContext is ready to go away.  (Note,
        // WCF checks IsIdle again when we call back).
        //
        // We do not want an instance to go away while another message is coming in, so we do some
        // work to make sure we hold a lock when we allow the instance to go idle.  info.IsIdle
        // keeps track of whether we are idle and have that lock.
        //
        bool IInstanceContextProvider.IsIdle(InstanceContext instanceContext)
        {
            AddressableInstanceContextInfo info = instanceContext.Extensions.Find<AddressableInstanceContextInfo>();
            return info.IsIdle;
        }

        //
        // WCF calls this if we return false from IsIdle.  The job of this method is to call the
        // callback when the InstanceContext is ready to close.  This allows you to keep an instance
        // alive as long as you want.
        //
        // When you call the callback, WCF will check again if there are attached channels or active
        // messages, and will call our IsIdle again.  If there are no channels or messages and we
        // return true, WCF closes the InstanceContext.  Otherwise, if there are channels or
        // messages, nothing happens--later when those channels/messages go away we will get another
        // round of IsIdle/NotifyIdle calls.
        //
        void IInstanceContextProvider.NotifyIdle(InstanceContextIdleCallback callback,
                                                 InstanceContext instanceContext)
        {
            AddressableInstanceContextInfo info = instanceContext.Extensions.Find<AddressableInstanceContextInfo>();
            info.SetIdleCallback(callback);
        }

        //
        // WCF calls this after receiving a request, before calling user code.  The value we return
        // from this method is passed to our BeforeSendReply below, and allows us to correlate the
        // two calls to a single message.
        //
        object IDispatchMessageInspector.AfterReceiveRequest(ref Message request, IClientChannel channel,
                                                             InstanceContext instanceContext)
        {
            // We will need the instanceContext in BeforeSendReply, so return it.
            return instanceContext;
        }

        //
        // WCF calls this after calling user code, just before sending back a reply (if applicable--
        // one-way operations behave the same as request-reply operations but just don't send
        // anything back).
        //
        void IDispatchMessageInspector.BeforeSendReply(ref Message reply, object correlationState)
        {
            // This is the object we returned from AfterReceiveRequest.
            InstanceContext instanceContext = (InstanceContext)correlationState;
            AddressableInstanceContextInfo info = instanceContext.Extensions.Find<AddressableInstanceContextInfo>();

            if (info != null)
            {
                // This message is done with this InstanceContext.
                info.DecrementBusyCount();
            }
        }

        internal void CallIdleCallback(InstanceContextIdleCallback callback, InstanceContext instanceContext)
        {
            lock (this.ThisLock)
            {
                // Take the lock while WCF closes the InstanceContext.  This prevents a new message
                // from picking up the dying InstanceContext.  A message that comes in after we
                // acquire the lock will not find an InstanceContext in the table, and will create a
                // new one.  If a new message came in before we took this lock and attached to the
                // instanceContext, this callback will be a no-op.  This ensures that when the
                // close-on-idle actually happens, it happens under this lock.
                callback(instanceContext);
            }
        }
    }
}