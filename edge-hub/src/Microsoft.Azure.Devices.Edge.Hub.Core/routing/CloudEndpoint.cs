﻿// Copyright (c) Microsoft. All rights reserved.
namespace Microsoft.Azure.Devices.Edge.Hub.Core.Routing
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Edge.Hub.Core.Cloud;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Azure.Devices.Routing.Core;
    using Microsoft.Azure.Devices.Routing.Core.TransientFaultHandling;
    using Microsoft.Azure.Devices.Routing.Core.Util;
    using Endpoint = Microsoft.Azure.Devices.Routing.Core.Endpoint;
    using IMessage = Microsoft.Azure.Devices.Edge.Hub.Core.IMessage;
    using IProcessor = Microsoft.Azure.Devices.Routing.Core.IProcessor;
    using IRoutingMessage = Microsoft.Azure.Devices.Routing.Core.IMessage;
    using ISinkResult = Microsoft.Azure.Devices.Routing.Core.ISinkResult<Microsoft.Azure.Devices.Routing.Core.IMessage>;
    using Option = Microsoft.Azure.Devices.Edge.Util.Option;
    using SystemProperties = Microsoft.Azure.Devices.Edge.Hub.Core.SystemProperties;

    public class CloudEndpoint : Endpoint
    {
        readonly Func<string, Util.Option<ICloudProxy>> cloudProxyGetterFunc;
        readonly Core.IMessageConverter<IRoutingMessage> messageConverter;
        
        public CloudEndpoint(string id, Func<string, Util.Option<ICloudProxy>> cloudProxyGetterFunc, Core.IMessageConverter<IRoutingMessage> messageConverter)
            : base(id)
        {
            this.cloudProxyGetterFunc = Preconditions.CheckNotNull(cloudProxyGetterFunc);
            this.messageConverter = Preconditions.CheckNotNull(messageConverter);
        }

        public override string Type => this.GetType().Name;

        public override IProcessor CreateProcessor() => new CloudMessageProcessor(this);

        public override void LogUserMetrics(long messageCount, long latencyInMs)
        {
            // TODO - No-op for now
        }

        class CloudMessageProcessor : IProcessor
        {
            readonly CloudEndpoint cloudEndpoint;

            public CloudMessageProcessor(CloudEndpoint endpoint)
            {
                this.cloudEndpoint = Preconditions.CheckNotNull(endpoint);
            }

            public async Task<ISinkResult> ProcessAsync(IRoutingMessage routingMessage, CancellationToken token)
            {
                // TODO - figure out if we can use cancellation token to cancel send
                var succeded = new List<IRoutingMessage>();
                var failed = new List<IRoutingMessage>();
                var invalid = new List<InvalidDetails<IRoutingMessage>>();

                IMessage message = this.cloudEndpoint.messageConverter.ToMessage(Preconditions.CheckNotNull(routingMessage, nameof(routingMessage)));
                await this.GetCloudProxy(routingMessage)
                    .Match(
                        async (c) =>
                        {
                            bool result = await c.SendMessageAsync(message);
                            if (result)
                            {
                                succeded.Add(routingMessage);
                            }
                            else
                            {
                                failed.Add(routingMessage);
                            }
                        },
                        () =>
                        {
                            // TODO - Check if this should be failed instead. 
                            invalid.Add(new InvalidDetails<IRoutingMessage>(routingMessage, FailureKind.InternalError));
                            return TaskEx.Done;
                        });

                return new SinkResult<IRoutingMessage>(succeded, failed, invalid, null);
            }

            public async Task<ISinkResult> ProcessAsync(ICollection<IRoutingMessage> routingMessages, CancellationToken token)
            {
                var succeded = new List<IRoutingMessage>();
                var failed = new List<IRoutingMessage>();
                var invalid = new List<InvalidDetails<IRoutingMessage>>();

                foreach (IRoutingMessage routingMessage in Preconditions.CheckNotNull(routingMessages, nameof(routingMessages)))
                {
                    ISinkResult res = await this.ProcessAsync(routingMessage, token);
                    succeded.AddRange(res.Succeeded);
                    failed.AddRange(res.Failed);
                    invalid.AddRange(res.InvalidDetailsList);
                }

                return new SinkResult<IRoutingMessage>(succeded, failed, invalid, null);
            }

            public Task CloseAsync(CancellationToken token)
            {
                // TODO - No-op
                return TaskEx.Done;
            }

            public Endpoint Endpoint => this.cloudEndpoint;

            public ITransientErrorDetectionStrategy ErrorDetectionStrategy => new ErrorDetectionStrategy(_ => false);

            Util.Option<ICloudProxy> GetCloudProxy(IRoutingMessage routingMessage) => routingMessage.SystemProperties.TryGetValue(SystemProperties.DeviceId, out string id)
                ? this.cloudEndpoint.cloudProxyGetterFunc(id)
                : Option.None<ICloudProxy>();
        }
    }
}