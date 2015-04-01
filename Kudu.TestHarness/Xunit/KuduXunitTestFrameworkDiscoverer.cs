using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Kudu.TestHarness.Xunit
{
    class KuduXunitTestFrameworkDiscoverer : XunitTestFrameworkDiscoverer
    {
        
        public KuduXunitTestFrameworkDiscoverer(IAssemblyInfo assemblyInfo,
                                                ISourceInformationProvider sourceProvider,
                                                IMessageSink diagnosticMessageSink,
                                                IXunitTestCollectionFactory collectionFactory = null)
            : base(assemblyInfo, sourceProvider, diagnosticMessageSink, collectionFactory)
        {
        }

        protected override bool FindTestsForMethod(ITestMethod testMethod, bool includeSourceInformation, IMessageBus messageBus, ITestFrameworkDiscoveryOptions discoveryOptions)
        {
            var factAttribute = testMethod.Method.GetCustomAttributes(typeof(FactAttribute)).FirstOrDefault();
            if (factAttribute == null)
            {
                return base.FindTestsForMethod(testMethod, includeSourceInformation, messageBus, discoveryOptions);
            }

            var defaultMethodDisplay = discoveryOptions.MethodDisplayOrDefault();
            var testAttribute = testMethod.TestClass.Class.GetCustomAttributes(typeof(KuduXunitTestClassAttribute)).FirstOrDefault();

            var theoryAttribute = testMethod.Method.GetCustomAttributes(typeof(TheoryAttribute)).FirstOrDefault();
            if (theoryAttribute == null)
            {
                // src\xunit.execution\Sdk\Frameworks\FactDiscoverer.cs
                if (testMethod.Method.GetParameters().Any())
                {
                    return base.FindTestsForMethod(testMethod, includeSourceInformation, messageBus, discoveryOptions);
                }

                var testCase = new KuduXunitTestCase(DiagnosticMessageSink, defaultMethodDisplay, testMethod, null, testAttribute);
                if (!ReportDiscoveredTestCase(testCase, includeSourceInformation, messageBus))
                {
                    return false;
                }
            }
            else
            {
                // if no [KuduXunitTestClass], fallback to default behavior
                if (testAttribute == null)
                {
                    var theoryDiscoverer = new TheoryDiscoverer(DiagnosticMessageSink);
                    foreach (var testCase in theoryDiscoverer.Discover(discoveryOptions, testMethod, theoryAttribute))
                    {
                        if (!ReportDiscoveredTestCase(testCase, includeSourceInformation, messageBus))
                        {
                            return false;
                        }
                    }
                }
                else
                {
                    // src\xunit.execution\Sdk\Frameworks\TheoryDiscoverer.cs
                    var testCases = new List<IXunitTestCase>();
                    var dataAttributes = testMethod.Method.GetCustomAttributes(typeof(DataAttribute));

                    foreach (var dataAttribute in dataAttributes)
                    {
                        var discovererAttribute = dataAttribute.GetCustomAttributes(typeof(DataDiscovererAttribute)).First();
                        var discoverer = ExtensibilityPointFactory.GetDataDiscoverer(DiagnosticMessageSink, discovererAttribute);

                        // GetData may return null, but that's okay; we'll let the NullRef happen and then catch it
                        // down below so that we get the composite test case.
                        foreach (var dataRow in discoverer.GetData(dataAttribute, testMethod.Method))
                        {
                            // Attempt to serialize the test case, since we need a way to uniquely identify a test
                            // and serialization is the best way to do that. If it's not serializable, this will
                            // throw and we will fall back to a single theory test case that gets its data
                            // at runtime.
                            testCases.Add(new KuduXunitTestCase(DiagnosticMessageSink, defaultMethodDisplay, testMethod, dataRow, testAttribute));
                        }
                    }

                    if (testCases.Count == 0)
                    {
                        testCases.Add(new ExecutionErrorTestCase(DiagnosticMessageSink, defaultMethodDisplay, testMethod,
                                                               String.Format("No data found for {0}.{1}", testMethod.TestClass.Class.Name, testMethod.Method.Name)));
                    }

                    foreach (var testCase in testCases)
                    {
                        if (!ReportDiscoveredTestCase(testCase, includeSourceInformation, messageBus))
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }
    }
}
