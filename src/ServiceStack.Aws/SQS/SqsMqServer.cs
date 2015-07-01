﻿using System;
using System.Collections.Generic;
using System.Linq;
using Amazon;
using ServiceStack.Aws.Interfaces;
using ServiceStack.Aws.Support;
using ServiceStack.Messaging;

namespace ServiceStack.Aws.SQS
{
    public class SqsMqServer : BaseMqServer<SqsMqWorker>
    {
        private readonly Dictionary<Type, SqsMqWorkerInfo> _handlerMap = new Dictionary<Type, SqsMqWorkerInfo>();

        private readonly ISqsMqMessageFactory _sqsMqMessageFactory;

        public SqsMqServer() : this(new SqsConnectionFactory()) { }

        public SqsMqServer(string awsAccessKey, string awsSecretKey, RegionEndpoint region)
            : this(new SqsConnectionFactory(awsAccessKey, awsSecretKey, region)) { }

        public SqsMqServer(SqsConnectionFactory sqsConnectionFactory) : this(new SqsMqMessageFactory(new SqsQueueManager(sqsConnectionFactory))) { }

        public SqsMqServer(ISqsMqMessageFactory sqsMqMessageFactory)
        {
            Guard.AgainstNullArgument(sqsMqMessageFactory, "sqsMqMessageFactory");

            _sqsMqMessageFactory = sqsMqMessageFactory;
        }
        
        public override IMessageFactory MessageFactory
        {
            get { return _sqsMqMessageFactory; }
        }

        /// <summary>
        /// How many times a message should be retried before sending to the DLQ (Max of 1000).
        /// </summary>
        public int RetryCount
        {
            get { return _sqsMqMessageFactory.RetryCount; }
            set { _sqsMqMessageFactory.RetryCount = value; }
        }

        /// <summary>
        /// How often, in seconds, any buffered SQS data is forced to be processed by the client. Only valid if buffering
        /// is enabled for a given model/server. By default, this is off entirely, which means if you are using buffering,
        /// data will only be processed to the server when operations occur that push a given queue/type over the
        /// configured size of the buffer.
        /// </summary>
        public int BufferFlushIntervalSeconds
        {
            get { return _sqsMqMessageFactory.BufferFlushIntervalSeconds; }
            set { _sqsMqMessageFactory.BufferFlushIntervalSeconds = value; }
        }

        /// <summary>
        /// Default time (in seconds) each in-flight message remains locked/unavailable on the queue before being returned to visibility
        /// Default of 30 seconds
        /// See http://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/AboutVT.html
        /// </summary>
        public int VisibilityTimeout
        {
            get { return _sqsMqMessageFactory.QueueManager.DefaultVisibilityTimeout; }
            set
            {
                Guard.AgainstArgumentOutOfRange(value < 0 || value > SqsQueueDefinition.MaxVisibilityTimeoutSeconds,
                                                "SQS MQ VisibilityTimeout must be 0-43200");

                _sqsMqMessageFactory.QueueManager.DefaultVisibilityTimeout = value;
            }
        }

        /// <summary>
        /// Defaut time (in seconds) each request to receive from the queue waits for a message to arrive
        /// Default is 0 seconds
        /// See http://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-long-polling.html
        /// </summary>
        public int ReceiveWaitTime
        {
            get { return _sqsMqMessageFactory.QueueManager.DefaultReceiveWaitTime; }
            set
            {
                Guard.AgainstArgumentOutOfRange(value < 0 || value > SqsQueueDefinition.MaxWaitTimeSeconds,
                                                "SQS MQ ReceiveWaitTime must be 0-20");

                _sqsMqMessageFactory.QueueManager.DefaultReceiveWaitTime = value;
            }
        }

        /// <summary>
        /// Disables buffering of send/delete/change/receive calls to SQS (call per request when disabled)
        /// </summary>
        public bool DisableBuffering
        {
            get { return _sqsMqMessageFactory.QueueManager.DisableBuffering; }
            set { _sqsMqMessageFactory.QueueManager.DisableBuffering = value; }
        }

        /// <summary>
        /// Execute global transformation or custom logic before a request is processed.
        /// Must be thread-safe.
        /// </summary>
        public Func<IMessage, IMessage> RequestFilter { get; set; }

        /// <summary>
        /// Execute global transformation or custom logic on the response.
        /// Must be thread-safe.
        /// </summary>
        public Func<object, object> ResponseFilter { get; set; }

        protected override void DoDispose()
        {
            try
            {
                _sqsMqMessageFactory.Dispose();
            }
            catch (Exception ex)
            {
                if (this.ErrorHandler != null)
                {
                    this.ErrorHandler(ex);
                }
            }
        }

        public override void RegisterHandler<T>(Func<IMessage<T>, object> processMessageFn)
        {
            RegisterHandler(processMessageFn, null, noOfThreads: 1);
        }

        public void RegisterHandler<T>(Func<IMessage<T>, object> processMessageFn, int noOfThreads,
                                       int? retryCount = null, int? visibilityTimeoutSeconds = null,
                                       int? receiveWaitTimeSeconds = null)
        {
            RegisterHandler(processMessageFn, null, noOfThreads, retryCount, visibilityTimeoutSeconds, receiveWaitTimeSeconds);
        }

        public override void RegisterHandler<T>(Func<IMessage<T>, object> processMessageFn,
                                                Action<IMessageHandler, IMessage<T>, Exception> processExceptionEx)
        {
            RegisterHandler(processMessageFn, processExceptionEx, noOfThreads: 1);
        }

        public void RegisterHandler<T>(Func<IMessage<T>, object> processMessageFn,
                                       Action<IMessageHandler, IMessage<T>, Exception> processExceptionEx,
                                       int noOfThreads, int? retryCount = null,
                                       int? visibilityTimeoutSeconds = null, int? receiveWaitTimeSeconds = null,
                                       bool? disableBuffering = null)
        {
            var type = typeof(T);

            Guard.Against<ArgumentException>(_handlerMap.ContainsKey(type), String.Concat("SQS Message handler has already been registered for type: ", type.Name));

            var retry = RetryCount = retryCount.HasValue
                                         ? retryCount.Value
                                         : RetryCount;

            _handlerMap[type] = new SqsMqWorkerInfo
                                {
                                    MessageHandlerFactory = new MessageHandlerFactory<T>(this, processMessageFn, processExceptionEx)
                                                            {
                                                                RequestFilter = this.RequestFilter,
                                                                ResponseFilter = this.ResponseFilter,
                                                                PublishResponsesWhitelist = this.PublishResponsesWhitelist,
                                                                RetryCount = retry
                                                            },
                                    MessageType = type,
                                    RetryCount = retry,
                                    ThreadCount = noOfThreads,
                                    VisibilityTimeout = visibilityTimeoutSeconds.HasValue
                                                            ? visibilityTimeoutSeconds.Value
                                                            : this.VisibilityTimeout,
                                    ReceiveWaitTime = receiveWaitTimeSeconds.HasValue
                                                          ? receiveWaitTimeSeconds.Value
                                                          : this.ReceiveWaitTime,
                                    DisableBuffering = disableBuffering.HasValue
                                                           ? disableBuffering.Value
                                                           : this.DisableBuffering
                                };

            LicenseUtils.AssertValidUsage(LicenseFeature.ServiceStack, QuotaType.Operations, _handlerMap.Count);
        }
        
        protected override void Init()
        {
            if (_workers != null)
            {
                return;
            }

            _sqsMqMessageFactory.ErrorHandler = this.ErrorHandler;

            _workers = new List<SqsMqWorker>();

            foreach (var handler in _handlerMap)
            {
                var msgType = handler.Key;
                var info = handler.Value;

                // First build the DLQ that will become the redrive policy for other q's for this type
                var dlqDefinition = _sqsMqMessageFactory.QueueManager.CreateQueue(info.QueueNames.Dlq, info);

                // Base in q and workers
                _sqsMqMessageFactory.QueueManager.CreateQueue(info.QueueNames.In, info, dlqDefinition.QueueArn);

                info.ThreadCount.Times(i => _workers.Add(new SqsMqWorker(_sqsMqMessageFactory, info,
                                                                         info.QueueNames.In, WorkerErrorHandler)));
                
                // Need an outq?
                if (PublishResponsesWhitelist == null || PublishResponsesWhitelist.Any(x => x == msgType.Name))
                {
                    _sqsMqMessageFactory.QueueManager.CreateQueue(info.QueueNames.Out, info, dlqDefinition.QueueArn);
                }
                
                // Priority q and workers
                if (PriortyQueuesWhitelist == null || PriortyQueuesWhitelist.Any(x => x == msgType.Name))
                {   // Need priority queue and workers
                    _sqsMqMessageFactory.QueueManager.CreateQueue(info.QueueNames.Priority, info, dlqDefinition.QueueArn);

                    info.ThreadCount.Times(i => _workers.Add(new SqsMqWorker(_sqsMqMessageFactory, info,
                                                                             info.QueueNames.Priority, WorkerErrorHandler)));
                }

            }
        }


    }
}