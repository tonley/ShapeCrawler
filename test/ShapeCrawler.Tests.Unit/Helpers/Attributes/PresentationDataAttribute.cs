﻿namespace ShapeCrawler.Tests.Unit.Helpers.Attributes;

using System;
using System.Collections.Generic;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;
using NUnit.Framework.Internal.Builders;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public class PresentationDataAttribute : Attribute, ITestBuilder
{
    private readonly string pptxName;

    public PresentationDataAttribute(string pptxName)
    {
        this.pptxName = pptxName;
    }

    public IEnumerable<TestMethod> BuildFrom(IMethodInfo method, Test suite)
    {
        IPresentation pres;
        if (this.pptxName == "new")
        {
            pres = SCPresentation.Create();
        }
        else
        {
            var pptxStream = SCTest.GetTestStream(this.pptxName);
            pres = SCPresentation.Open(pptxStream);
        }

        var parameters = new TestCaseParameters(new object[] { pres });

        yield return new NUnitTestCaseBuilder().BuildTestMethod(method, suite, parameters);
    }
}