using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Kudu.TestHarness.Xunit
{
    [Serializable]
    public class KuduXunitTestCase : XunitTestCase
    {
        private bool _disableRetry;

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Called by the de-serializer", true)]
        public KuduXunitTestCase() { }

        public KuduXunitTestCase(IMessageSink diagnosticMessageSink, 
                                 TestMethodDisplay testMethodDisplay, 
                                 ITestMethod testMethod,
                                 object[] testMethodArguments,
                                 IAttributeInfo testAttribute)
            : base(diagnosticMessageSink, testMethodDisplay, testMethod, testMethodArguments)
        {
            _disableRetry = testAttribute == null ? true : testAttribute.GetNamedArgument<bool>("DisableRetry");
        }

        // src\xunit.execution\Sdk\Frameworks\TestMethodTestCase.cs
        protected override string GetUniqueID()
        {
            using (var stream = new System.IO.MemoryStream())
            {
                Write(stream, TestMethod.TestClass.TestCollection.TestAssembly.Assembly.Name);
                Write(stream, TestMethod.TestClass.Class.Name);
                Write(stream, TestMethod.Method.Name);

                if (TestMethodArguments != null)
                {
                    // Work around test arg issue
                    Write(stream, DisplayName);
                    // Write(stream, SerializationHelper.Serialize(TestMethodArguments));
                }

                stream.Position = 0;

                using (var sha1 = new SHA1Managed())
                {
                    var hash = sha1.ComputeHash(stream);
                    return String.Join("", hash.Select(x => x.ToString("x2")).ToArray());
                }
            }
        }

        // This method is called by the xUnit test framework classes to run the test case. We will do the
        // loop here, forwarding on to the implementation in XunitTestCase to do the heavy lifting. We will
        // continue to re-run the test until the aggregator has an error (meaning that some internal error
        // condition happened), or the test runs without failure, or we've hit the maximum number of tries.
        public override async Task<RunSummary> RunAsync(IMessageSink diagnosticMessageSink,
                                                        IMessageBus messageBus,
                                                        object[] constructorArguments,
                                                        ExceptionAggregator aggregator,
                                                        CancellationTokenSource cancellationTokenSource)
        {
            if (_disableRetry)
            {
                return await base.RunAsync(diagnosticMessageSink, messageBus, constructorArguments, aggregator, cancellationTokenSource);
            }

            var runCount = 0;

            // This is really the only tricky bit: we need to capture and delay messages (since those will
            // contain run status) until we know we've decided to accept the final result;
            var delayedMessageBus = new DelayedMessageBus(messageBus);

            while (true)
            {
                var summary = await base.RunAsync(diagnosticMessageSink, runCount == 0 ? delayedMessageBus : messageBus, constructorArguments, aggregator, cancellationTokenSource);
                if (aggregator.HasExceptions || summary.Failed == 0 || ++runCount >= KuduXunitConstants.MaxRetries)
                {
                    // Flush all the delayed messages
                    delayedMessageBus.Flush(summary.Failed == 0);
                    return summary;
                }

                diagnosticMessageSink.OnMessage(new DiagnosticMessage("Execution of '{0}' failed (attempt #{1}), retrying...", DisplayName, runCount));
            }
        }

        public override void Serialize(IXunitSerializationInfo data)
        {
            base.Serialize(data);

            data.AddValue("DisableRetry", _disableRetry);
        }

        public override void Deserialize(IXunitSerializationInfo data)
        {
            base.Deserialize(data);

            _disableRetry = data.GetValue<bool>("DisableRetry");
        }

        static void Write(Stream stream, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
            stream.WriteByte(0);
        }

        public class DelayedMessageBus : IMessageBus
        {
            private readonly IMessageBus innerBus;
            private readonly List<IMessageSinkMessage> messages = new List<IMessageSinkMessage>();

            public DelayedMessageBus(IMessageBus innerBus)
            {
                this.innerBus = innerBus;
            }

            public bool QueueMessage(IMessageSinkMessage message)
            {
                lock (messages)
                {
                    messages.Add(message);
                }

                // No way to ask the inner bus if they want to cancel without sending them the message, so
                // we just go ahead and continue always.
                return true;
            }

            public void Dispose()
            {
            }

            public void Flush(bool retrySucceeded)
            {
                foreach (var message in messages)
                {
                    if (!retrySucceeded)
                    {
                        innerBus.QueueMessage(message);
                    }
                    else
                    {
                        var failed = message as TestFailed;
                        if (failed == null)
                        {
                            innerBus.QueueMessage(message);
                        }
                        else
                        {
                            // in case of retry succeeded, convert all failure to skip (ignored)
                            var reason = new StringBuilder();
                            reason.AppendLine(String.Join(Environment.NewLine, failed.ExceptionTypes));
                            reason.AppendLine(String.Join(Environment.NewLine, failed.Messages));
                            reason.AppendLine(String.Join(Environment.NewLine, failed.StackTraces));
                            innerBus.QueueMessage(new TestSkipped(failed.Test, reason.ToString()));
                        }
                    }
                }
            }
        }
    }
}
